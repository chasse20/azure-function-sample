using Azure.Storage.Blobs;
using DataService.SQL;
using DocuSignModel = DocuSign.eSign.Model;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.DurableTask;
using Microsoft.DurableTask.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace DataService.Function.Envelope
{
	public partial class Function
	{
		[Function( nameof( PatchSendEnvelopeAsync ) )]
		public async Task<HttpResponseData> PatchSendEnvelopeAsync( [HttpTrigger( AuthorizationLevel.Anonymous, "patch", Route = "envelope/{id}/send" )] HttpRequestData tRequest, int id, [DurableClient] DurableTaskClient tStarter, CancellationToken tCancel )
		{
			// Authenticate
			HttpResponseData tempResponse = AuthorizationUtility.GetAuthorizationResponse( tRequest, REQUIRED_ROLE );

			if ( tempResponse != null )
			{
				return tempResponse;
			}

			// Envelope
			SendRequest tempRequest = await tRequest.ReadFromJsonAsync<SendRequest>( tCancel );

			if ( tempRequest == null || tempRequest.SenderId <= 0 || string.IsNullOrWhiteSpace( tempRequest.Name ) || string.IsNullOrWhiteSpace( tempRequest.RecipientName ) || string.IsNullOrWhiteSpace( tempRequest.RecipientEmail ) )
			{
				return tRequest.CreateResponse( HttpStatusCode.BadRequest );
			}

			await using Context tempContext = await _DB.CreateDbContextAsync( tCancel );
			SQL.Envelope tempEnvelope = await tempContext.Envelope.Include( x => x.EnvelopeFiles ).SingleOrDefaultAsync( x => x.EnvelopeId == id, tCancel );

			if ( tempEnvelope == null )
			{
				return tRequest.CreateResponse( HttpStatusCode.NotFound );
			}

			// Validate status, only allow sending if the envelope is in Draft or Sending status
			EnvelopeStatus? tempStatus = GetEnvelopeStatus( tempEnvelope );

			if ( tempStatus is EnvelopeStatus.Sent or EnvelopeStatus.Signed )
			{
				tempResponse = tRequest.CreateResponse( HttpStatusCode.OK );
				await tempResponse.WriteAsJsonAsync( tempEnvelope, tCancel );
				return tempResponse;
			}
			else if ( tempStatus is not EnvelopeStatus.Draft and not EnvelopeStatus.Sending )
			{
				return tRequest.CreateResponse( HttpStatusCode.Conflict );
			}
			else if ( tempEnvelope.EnvelopeFiles == null || tempEnvelope.EnvelopeFiles.Count == 0 )
			{
				return tRequest.CreateResponse( HttpStatusCode.BadRequest );
			}

			// Mark Sender and run orchestration
			tempEnvelope.SenderId = tempRequest.SenderId;
			tempEnvelope.Name = tempRequest.Name.Trim();
			await tempContext.SaveChangesAsync( tCancel );

			string tempInstanceId = $"EnvelopeSend{tempEnvelope.EnvelopeId}";
			OrchestrationMetadata tempMetadata = await tStarter.GetInstanceAsync( tempInstanceId, tCancel );

			if ( tempMetadata?.GetIsRunning() == true )
			{
				return tRequest.CreateResponse( HttpStatusCode.Accepted );
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
				nameof( OrchestrateEnvelopeSendAsync ),
				new SendInput()
				{
					EnvelopeId = tempEnvelope.EnvelopeId,
					Date = DateTime.UtcNow,
					RecipientName = tempRequest.RecipientName,
					RecipientEmail = tempRequest.RecipientEmail
				},
				new StartOrchestrationOptions( tempInstanceId ),
				tCancel
			);

			return tRequest.CreateResponse( HttpStatusCode.Accepted );
		}

		[Function( nameof( OrchestrateEnvelopeSendAsync ) )]
		public async Task OrchestrateEnvelopeSendAsync( [OrchestrationTrigger] TaskOrchestrationContext tContext )
		{
			SendInput tempInput = tContext.GetInput<SendInput>();
			string tempDocuSignEnvelopeId = null;

			try
			{
				tempDocuSignEnvelopeId = await tContext.CallActivityAsync<string>( nameof( SendDocuSignEnvelopeAsync ), tempInput, _taskOptions );
			}
			finally
			{
				await tContext.CallActivityAsync( nameof( UpdateEnvelopeSentStatusAsync ), new SentStatusInput() { EnvelopeId = tempInput.EnvelopeId, DocuSignEnvelopeId = tempDocuSignEnvelopeId }, _taskOptions );
			}
		}

		[Function( nameof( SendDocuSignEnvelopeAsync ) )]
		public async Task<string> SendDocuSignEnvelopeAsync( [ActivityTrigger] SendInput tInput, CancellationToken tCancel )
		{
			tCancel.ThrowIfCancellationRequested();

			try
			{
				await using Context tempContext = await _DB.CreateDbContextAsync( tCancel );
				SQL.Envelope tempEnvelope = await tempContext.Envelope.AsNoTracking().Include( x => x.EnvelopeFiles ).ThenInclude( x => x.File ).SingleOrDefaultAsync( x => x.EnvelopeId == tInput.EnvelopeId, tCancel );

				if ( tempEnvelope == null || GetEnvelopeStatus( tempEnvelope ) != EnvelopeStatus.Sending )
				{
					return null;
				}

				List<DocuSignModel.Document> tempDocuments = await GetDocumentsAsync( _blob, tempEnvelope.EnvelopeFiles, tCancel );
				DocuSignModel.EnvelopeDefinition tempDefinition = new()
				{
					EmailSubject = tempEnvelope.Name,
					Documents = tempDocuments,
					Recipients = new()
					{
						Signers =
						[
							new()
							{
								Name = tInput.RecipientName,
								Email = tInput.RecipientEmail,
								RecipientId = "1",
								RoutingOrder = "1"
							}
						]
					},
					Status = "sent"
				};

				DocuSignModel.EnvelopeSummary tempSummary = await _docuSign.CreateEnvelopeAsync( tempDefinition, tCancel );
				return tempSummary.EnvelopeId;
			}
			catch ( OperationCanceledException ) when ( tCancel.IsCancellationRequested )
			{
				throw;
			}
			catch ( Exception tException )
			{
				_logger.LogError( tException, "Failed to send EnvelopeId {EnvelopeId} through DocuSign.", tInput.EnvelopeId );
				throw;
			}
		}

		[Function( nameof( UpdateEnvelopeSentStatusAsync ) )]
		public async Task UpdateEnvelopeSentStatusAsync( [ActivityTrigger] SentStatusInput tInput, CancellationToken tCancel )
		{
			tCancel.ThrowIfCancellationRequested();

			await using Context tempContext = await _DB.CreateDbContextAsync( tCancel );
			SQL.Envelope tempEnvelope = await tempContext.Envelope.FindAsync( [ tInput.EnvelopeId ], tCancel );

			if ( tempEnvelope == null )
			{
				_logger.LogWarning( "EnvelopeId {EnvelopeId} was not found while updating its send status.", tInput.EnvelopeId );
				return;
			}

			if ( string.IsNullOrWhiteSpace( tInput.DocuSignEnvelopeId ) )
			{
				tempEnvelope.SenderId = null;
			}
			else
			{
				tempEnvelope.DocusignEnvelopeId = tInput.DocuSignEnvelopeId;
				tempEnvelope.SentDate ??= DateTime.UtcNow;
			}

			await tempContext.SaveChangesAsync( tCancel );
		}

		public static async Task<List<DocuSignModel.Document>> GetDocumentsAsync( BlobContainerClient tBlob, ICollection<EnvelopeFile> tFiles, CancellationToken tCancel )
		{
			tCancel.ThrowIfCancellationRequested();

			if ( tFiles == null || tFiles.Count == 0 )
			{
				return [];
			}

			List<DocuSignModel.Document> tempDocuments = [];
			int tempDocumentId = 1;

			foreach ( EnvelopeFile tempEnvelopeFile in tFiles.OrderBy( x => x.Sequence ) )
			{
				if ( tempEnvelopeFile.File == null )
				{
					continue;
				}

				BlobClient tempBlobClient = tBlob.GetBlobClient( tempEnvelopeFile.File.AzureBlobURI );
				await using Stream tempStream = ( await tempBlobClient.DownloadStreamingAsync( cancellationToken: tCancel ) ).Value.Content;
				using MemoryStream tempMemory = new();
				await tempStream.CopyToAsync( tempMemory, tCancel );

				string tempExtension = Path.GetExtension( tempEnvelopeFile.File.AzureBlobURI ).TrimStart( '.' ).ToLowerInvariant();

				if ( string.IsNullOrWhiteSpace( tempExtension ) )
				{
					throw new InvalidDataException( $"Blob '{tempEnvelopeFile.File.AzureBlobURI}' does not have a file extension." );
				}

				tempDocuments.Add
				(
					new()
					{
						DocumentBase64 = Convert.ToBase64String( tempMemory.ToArray() ),
						Name = tempEnvelopeFile.File.Name,
						FileExtension = tempExtension,
						DocumentId = tempDocumentId.ToString(),
						Order = tempDocumentId.ToString()
					}
				);

				++tempDocumentId;
			}

			return tempDocuments;
		}
	}
}
