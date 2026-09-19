using DataService.SQL;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.DurableTask;
using Microsoft.DurableTask.Client;
using Microsoft.EntityFrameworkCore;
using System;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace DataService.Function.Envelope
{
	public partial class Function
	{
		[Function( nameof( PatchVoidEnvelopeAsync ) )]
		public async Task<HttpResponseData> PatchVoidEnvelopeAsync( [HttpTrigger( AuthorizationLevel.Anonymous, "patch", Route = "envelope/{id}/void" )] HttpRequestData tRequest, int id, [DurableClient] DurableTaskClient tStarter, CancellationToken tCancel )
		{
			// Authenticate
			HttpResponseData tempResponse = AuthorizationUtility.GetAuthorizationResponse( tRequest, REQUIRED_ROLE );

			if ( tempResponse != null )
			{
				return tempResponse;
			}

			// Envelope
			await using Context tempContext = await _DB.CreateDbContextAsync( tCancel );
			SQL.Envelope tempEnvelope = await tempContext.Envelope.SingleOrDefaultAsync( x => x.EnvelopeId == id, tCancel );

			if ( tempEnvelope == null )
			{
				return tRequest.CreateResponse( HttpStatusCode.NotFound );
			}

			// Validate Status, only allow voiding if the envelope is in Sent or Voiding status
			EnvelopeStatus? tempStatus = GetEnvelopeStatus( tempEnvelope );

			if ( tempStatus == EnvelopeStatus.Voided )
			{
				return tRequest.CreateResponse( HttpStatusCode.OK );
			}
			else if ( tempStatus is not EnvelopeStatus.Sent and not EnvelopeStatus.Voiding )
			{
				return tRequest.CreateResponse( HttpStatusCode.Conflict );
			}

			if ( tempEnvelope.VoiderId == null )
			{
				tempEnvelope.VoiderId = 1; // Public sample placeholder for the authenticated actor ID.
				await tempContext.SaveChangesAsync( tCancel );
			}

			// Orchestration
			string tempInstanceId = $"EnvelopeVoid{tempEnvelope.EnvelopeId}";
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
				nameof( OrchestrateEnvelopeVoidAsync ),
				new Input() { EnvelopeId = tempEnvelope.EnvelopeId, Date = DateTime.UtcNow },
				new StartOrchestrationOptions( tempInstanceId ),
				tCancel
			);

			return tRequest.CreateResponse( HttpStatusCode.Accepted );
		}

		[Function( nameof( OrchestrateEnvelopeVoidAsync ) )]
		public async Task OrchestrateEnvelopeVoidAsync( [OrchestrationTrigger] TaskOrchestrationContext tContext )
		{
			Input tempInput = tContext.GetInput<Input>();
			bool tempIsVoided = false;

			try
			{
				tempIsVoided = await tContext.CallActivityAsync<bool>( nameof( VoidDocuSignEnvelopeAsync ), tempInput.EnvelopeId, _taskOptions );
			}
			finally
			{
				await tContext.CallActivityAsync( nameof( UpdateEnvelopeVoidStatusAsync ), new VoidInput() { EnvelopeId = tempInput.EnvelopeId, IsVoided = tempIsVoided, Date = tempInput.Date }, _taskOptions );
			}
		}

		[Function( nameof( VoidDocuSignEnvelopeAsync ) )]
		public async Task<bool> VoidDocuSignEnvelopeAsync( [ActivityTrigger] int tEnvelopeId, CancellationToken tCancel )
		{
			await using Context tempContext = await _DB.CreateDbContextAsync( tCancel );
			string tempDocuSignEnvelopeId = await tempContext.Envelope.AsNoTracking().Where( x => x.EnvelopeId == tEnvelopeId ).Select( x => x.DocusignEnvelopeId ).SingleOrDefaultAsync( tCancel );

			if ( string.IsNullOrWhiteSpace( tempDocuSignEnvelopeId ) )
			{
				return false;
			}

			await _docuSign.VoidEnvelopeAsync( tempDocuSignEnvelopeId, "Voided by application workflow.", tCancel );
			return true;
		}

		[Function( nameof( UpdateEnvelopeVoidStatusAsync ) )]
		public async Task UpdateEnvelopeVoidStatusAsync( [ActivityTrigger] VoidInput tInput, CancellationToken tCancel )
		{
			await using Context tempContext = await _DB.CreateDbContextAsync( tCancel );
			SQL.Envelope tempEnvelope = await tempContext.Envelope.FindAsync( [ tInput.EnvelopeId ], tCancel );

			if ( tempEnvelope == null )
			{
				return;
			}

			if ( tInput.IsVoided )
			{
				tempEnvelope.VoidedDate ??= tInput.Date;
			}
			else
			{
				tempEnvelope.VoiderId = null;
			}

			await tempContext.SaveChangesAsync( tCancel );
		}
	}
}
