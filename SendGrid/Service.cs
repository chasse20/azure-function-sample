using Microsoft.Extensions.Logging;
using SendGrid;
using SendGrid.Helpers.Mail;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DataService.SendGrid
{
	public class Service( ISendGridClient tClient, ILogger<Service> tLogger ) : IService
	{
		protected readonly ILogger<Service> _logger = tLogger;
		protected readonly ISendGridClient _client = tClient;

		public virtual async Task SendEmailAsync( string tSender, string tReceiver, string tTemplateId, Dictionary<string, object> tTemplateData, CancellationToken tCancel )
		{
			// Validate
			ArgumentException.ThrowIfNullOrWhiteSpace( tSender );
			ArgumentException.ThrowIfNullOrWhiteSpace( tReceiver );
			ArgumentException.ThrowIfNullOrWhiteSpace( tTemplateId );
			ArgumentNullException.ThrowIfNull( tTemplateData );
			tCancel.ThrowIfCancellationRequested();

			// Message
			SendGridMessage tempMessage = new()
			{
				From = new EmailAddress( tSender ),
				TemplateId = tTemplateId
			};

			tempMessage.AddTo( new EmailAddress( tReceiver ) );
			tempMessage.SetTemplateData( tTemplateData );

			// Send
			Response tempResponse;

			try
			{
				tempResponse = await _client.SendEmailAsync( tempMessage, tCancel );
			}
			catch ( OperationCanceledException ) when ( tCancel.IsCancellationRequested )
			{
				throw;
			}
			catch ( OperationCanceledException tException )
			{
				throw new EmailSubmissionException( "SendGrid email submission timed out.", null, tException );
			}
			catch ( Exception tException )
			{
				throw new EmailSubmissionException( "SendGrid email submission failed.", null, tException );
			}

			if ( tempResponse.IsSuccessStatusCode )
			{
				return;
			}

			// Error
			_logger.LogError( "SendGrid rejected email submission. StatusCode: {StatusCode}. TemplateId: {TemplateId}.", tempResponse.StatusCode, tTemplateId );

			throw new EmailSubmissionException( $"SendGrid rejected email submission with status code {(int)tempResponse.StatusCode}.", tempResponse.StatusCode );
		}
	}
}
