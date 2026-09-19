using DataService.DocuSign;
using DataService.Function.Envelope;
using DataService.SQL;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.DurableTask;
using Microsoft.DurableTask.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace DataService.Function.DocuSign
{
	public class Function( IDbContextFactory<Context> tDB, Azure.Storage.Blobs.BlobContainerClient tBlob, IService tDocuSign, SendGrid.IService tSendGrid, IConfiguration tConfig, TaskOptions tTaskOptions, ILogger<Function> tLogger )
	{
		protected readonly IDbContextFactory<Context> _DB = tDB;
		protected readonly Azure.Storage.Blobs.BlobContainerClient _blob = tBlob;
		protected readonly IService _docuSign = tDocuSign;
		protected readonly SendGrid.IService _sendGrid = tSendGrid;
		protected readonly IConfiguration _config = tConfig;
		protected readonly TaskOptions _taskOptions = tTaskOptions;
		protected readonly ILogger<Function> _logger = tLogger;

		[Function( nameof( PostEnvelopeStatusFromDocuSign ) )]
		public async Task<HttpResponseData> PostEnvelopeStatusFromDocuSign( [HttpTrigger( AuthorizationLevel.Function, "post", Route = "docuSign/webhook" )] HttpRequestData tRequest, [DurableClient] DurableTaskClient tStarter, CancellationToken tCancel )
		{
			EnvelopeEvent tempEvent = await tRequest.ReadFromJsonAsync<EnvelopeEvent>( tCancel );

			if ( tempEvent?.Data == null || string.IsNullOrWhiteSpace( tempEvent.Data.EnvelopeId ) || string.IsNullOrWhiteSpace( tempEvent.Event ) || tempEvent.GeneratedDateTime == default )
			{
				return tRequest.CreateResponse( HttpStatusCode.BadRequest );
			}

			const string tempEnvelopePrefix = "envelope-";

			if ( !tempEvent.Event.StartsWith( tempEnvelopePrefix, StringComparison.OrdinalIgnoreCase ) )
			{
				return tRequest.CreateResponse( HttpStatusCode.BadRequest );
			}

			EnvelopeStatus? tempNewStatus = Envelope.Function.GetEnvelopeStatus( tempEvent.Event[ tempEnvelopePrefix.Length.. ] );

			if ( !tempNewStatus.HasValue )
			{
				return tRequest.CreateResponse( HttpStatusCode.OK );
			}

			await using Context tempContext = await _DB.CreateDbContextAsync( tCancel );
			int? tempEnvelopeId = await tempContext.Envelope.AsNoTracking().Where( x => x.DocusignEnvelopeId == tempEvent.Data.EnvelopeId ).Select( x => (int?)x.EnvelopeId ).SingleOrDefaultAsync( tCancel );

			if ( !tempEnvelopeId.HasValue )
			{
				_logger.LogWarning( "DocuSign Envelope {DocuSignEnvelopeId} was not found for the webhook event.", tempEvent.Data.EnvelopeId );
				return tRequest.CreateResponse( HttpStatusCode.OK );
			}

			string tempInstanceId = $"DocuSignEnvelopeStatus{tempEnvelopeId.Value}-{tempNewStatus.Value}-{tempEvent.GeneratedDateTime.Ticks}";
			OrchestrationMetadata tempMetadata = await tStarter.GetInstanceAsync( tempInstanceId, tCancel );

			if ( tempMetadata?.GetIsRunning() == true || tempMetadata?.RuntimeStatus == OrchestrationRuntimeStatus.Completed )
			{
				return tRequest.CreateResponse( HttpStatusCode.OK );
			}
			else if ( tempMetadata != null )
			{
				if ( !tempMetadata.IsCompleted )
				{
					return tRequest.CreateResponse( HttpStatusCode.Conflict );
				}

				await tStarter.PurgeTerminalOrchestrationAsync( tempInstanceId, tempMetadata, tCancel );
			}

			await tStarter.ScheduleNewOrchestrationInstanceAsync
			(
				nameof( OrchestrateDocuSignEnvelopeStatusAsync ),
				new Input() { EnvelopeId = tempEnvelopeId.Value, NewStatus = tempNewStatus.Value, Date = tempEvent.GeneratedDateTime },
				new StartOrchestrationOptions( tempInstanceId ),
				tCancel
			);

			return tRequest.CreateResponse( HttpStatusCode.OK );
		}

		[Function( nameof( OrchestrateDocuSignEnvelopeStatusAsync ) )]
		public async Task OrchestrateDocuSignEnvelopeStatusAsync( [OrchestrationTrigger] TaskOrchestrationContext tContext )
		{
			Input tempInput = tContext.GetInput<Input>();

			if ( await tContext.CallActivityAsync<bool>( nameof( UpdateDocuSignEnvelopeStatusAsync ), tempInput, _taskOptions ) && tempInput.NewStatus == EnvelopeStatus.Signed )
			{
				await tContext.CallActivityAsync( nameof( ArchiveCompletedEnvelopeAsync ), tempInput.EnvelopeId, _taskOptions );
				await tContext.CallActivityAsync( nameof( SendEnvelopeCompletedEmailAsync ), tempInput.EnvelopeId, _taskOptions );
			}
		}

		[Function( nameof( UpdateDocuSignEnvelopeStatusAsync ) )]
		public virtual async Task<bool> UpdateDocuSignEnvelopeStatusAsync( [ActivityTrigger] Input tInput, CancellationToken tCancel )
		{
			await using Context tempContext = await _DB.CreateDbContextAsync( tCancel );
			SQL.Envelope tempEnvelope = await tempContext.Envelope.Include( x => x.EnvelopePreview ).ThenInclude( x => x.File ).SingleOrDefaultAsync( x => x.EnvelopeId == tInput.EnvelopeId, tCancel );

			if ( tempEnvelope == null )
			{
				return false;
			}

			// Check status
			EnvelopeStatus? tempOldStatus = Envelope.Function.GetEnvelopeStatus( tempEnvelope );

			if ( tempOldStatus == tInput.NewStatus )
			{
				return false;
			}
			else if ( tempOldStatus is EnvelopeStatus.Signed or EnvelopeStatus.Voided )
			{
				if ( tInput.NewStatus == EnvelopeStatus.Sent && !tempEnvelope.SentDate.HasValue )
				{
					tempEnvelope.SentDate = tInput.Date;
					await tempContext.SaveChangesAsync( tCancel );
				}

				return false;
			}

			// Update
			string tempPreviewBlobName = null;

			switch ( tInput.NewStatus )
			{
				case EnvelopeStatus.Sent:
					tempEnvelope.SentDate ??= tInput.Date;
					break;
				case EnvelopeStatus.Voided:
					tempEnvelope.VoidedDate ??= tInput.Date;
					break;
				case EnvelopeStatus.Signed:
					tempEnvelope.SignedDate ??= tInput.Date;
					break;
				case EnvelopeStatus.Draft:
					tempEnvelope.SenderId = null;
					tempEnvelope.SentDate = null;
					break;
			}

			if ( tInput.NewStatus != EnvelopeStatus.Draft && tempEnvelope.EnvelopePreview != null )
			{
				tempPreviewBlobName = await EnvelopePreview.Function.ProcessDeleteEnvelopePreviewAsync( tempContext, tempEnvelope.EnvelopePreview, tCancel );
				tempEnvelope.EnvelopePreview = null;
			}

			await tempContext.SaveChangesAsync( tCancel );

			// Clear any Previews
			if ( !string.IsNullOrWhiteSpace( tempPreviewBlobName ) )
			{
				await _blob.DeleteFileBlobAfterCommitAsync( tempPreviewBlobName, _logger, tCancel );
			}

			return true;
		}

		[Function( nameof( ArchiveCompletedEnvelopeAsync ) )]
		public async Task ArchiveCompletedEnvelopeAsync( [ActivityTrigger] int tEnvelopeId, CancellationToken tCancel )
		{
			await using Context tempContext = await _DB.CreateDbContextAsync( tCancel );
			var tempEnvelope = await tempContext.Envelope.AsNoTracking().Where( x => x.EnvelopeId == tEnvelopeId ).Select
			(
				x => new
				{
					x.EnvelopeId,
					x.Name,
					x.DocusignEnvelopeId
				}
			).SingleOrDefaultAsync( tCancel );

			if ( tempEnvelope == null || string.IsNullOrWhiteSpace( tempEnvelope.DocusignEnvelopeId ) )
			{
				return;
			}

			await using Stream tempStream = await _docuSign.GetCombinedDocumentAsync( tempEnvelope.DocusignEnvelopeId, tCancel );
			string tempBlobName = $"completed/{tempEnvelope.EnvelopeId}/{tempEnvelope.DocusignEnvelopeId}/Signed{FileExtension.PDF}";
			Dictionary<string, string> tempMetaData = new()
			{
				{ "Name", tempEnvelope.Name },
				{ "Envelope", tempEnvelope.EnvelopeId.ToString() }
			};

			await _blob.UploadFileBlobAsync( tempStream, tempBlobName, tempMetaData, tCancel );
		}

		[Function( nameof( SendEnvelopeCompletedEmailAsync ) )]
		public async Task SendEnvelopeCompletedEmailAsync( [ActivityTrigger] int tEnvelopeId, CancellationToken tCancel )
		{
			await using Context tempContext = await _DB.CreateDbContextAsync( tCancel );
			string tempEnvelopeName = await tempContext.Envelope.AsNoTracking().Where( x => x.EnvelopeId == tEnvelopeId ).Select( x => x.Name ).SingleOrDefaultAsync( tCancel );
			string tempSender = _config[ "SENDGRID_SENDER" ];
			string tempRecipient = _config[ "SENDGRID_NOTIFICATION_RECIPIENT" ];
			string tempTemplateId = _config[ "SENDGRID_ENVELOPE_COMPLETED_TEMPLATE_ID" ];

			if ( string.IsNullOrWhiteSpace( tempEnvelopeName ) || string.IsNullOrWhiteSpace( tempSender ) || string.IsNullOrWhiteSpace( tempRecipient ) || string.IsNullOrWhiteSpace( tempTemplateId ) )
			{
				return;
			}

			await _sendGrid.SendEmailAsync
			(
				tempSender,
				tempRecipient,
				tempTemplateId,
				new Dictionary<string, object>() { { "EnvelopeName", tempEnvelopeName } },
				tCancel
			);
		}
	}
}
