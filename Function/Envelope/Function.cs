using Azure.Storage.Blobs;
using DataService.SQL;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.DurableTask;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace DataService.Function.Envelope
{
	public partial class Function( IDbContextFactory<Context> tDB, BlobContainerClient tBlob, DataService.DocuSign.IService tDocuSign, TaskOptions tTaskOptions, ILogger<Function> tLogger )
	{
		protected readonly IDbContextFactory<Context> _DB = tDB;
		protected readonly BlobContainerClient _blob = tBlob;
		public readonly DataService.DocuSign.IService _docuSign = tDocuSign;
		protected readonly ILogger<Function> _logger = tLogger;
		protected readonly TaskOptions _taskOptions = tTaskOptions;
		protected static string REQUIRED_ROLE = "DocumentWorkflow.Access";

		public static EnvelopeStatus? GetEnvelopeStatus( string tDocuSignString )
		{
			switch ( tDocuSignString )
			{
				case "sent":
				case "delivered":
				case "resent":
					return EnvelopeStatus.Sent;
				case "signed":
				case "completed":
					return EnvelopeStatus.Signed;
				case "declined":
				case "deleted":
				case "voided":
					return EnvelopeStatus.Voided;
				default:
					break;
			}

			return null;
		}

		public static EnvelopeStatus? GetEnvelopeStatus( SQL.Envelope tEnvelope )
		{
			if ( tEnvelope != null )
			{
				if ( tEnvelope.VoidedDate != null )
				{
					return EnvelopeStatus.Voided;
				}
				else if ( tEnvelope.VoiderId != null )
				{
					return EnvelopeStatus.Voiding;
				}
				else if ( tEnvelope.SignedDate != null )
				{
					return EnvelopeStatus.Signed;
				}
				else if ( tEnvelope.SentDate != null )
				{
					return EnvelopeStatus.Sent;
				}
				else if ( tEnvelope.SenderId != null )
				{
					return EnvelopeStatus.Sending;
				}

				return EnvelopeStatus.Draft;
			}

			return null;
		}

		[Function( nameof( GetEnvelopeAsync ) )]
		public async Task<HttpResponseData> GetEnvelopeAsync( [HttpTrigger( AuthorizationLevel.Anonymous, "get", Route = "envelope/{id}" )] HttpRequestData tRequest, int id, CancellationToken tCancel )
		{
			// Authenticate
			HttpResponseData tempResponse = AuthorizationUtility.GetAuthorizationResponse( tRequest, REQUIRED_ROLE );

			if ( tempResponse != null )
			{
				return tempResponse;
			}

			// Envelope
			await using Context tempContext = await _DB.CreateDbContextAsync( tCancel );
			SQL.Envelope tempEnvelope = await tempContext.Envelope.AsNoTracking().Include( x => x.EnvelopePreview ).FirstOrDefaultAsync( x => x.EnvelopeId == id, tCancel );

			if ( tempEnvelope == null )
			{
				return tRequest.CreateResponse( HttpStatusCode.NotFound );
			}

			tempResponse = tRequest.CreateResponse( HttpStatusCode.OK );
			await tempResponse.WriteAsJsonAsync( tempEnvelope, tCancel );
			return tempResponse;
		}
	}
}
