using System;
using System.Net;

namespace DataService.SendGrid
{
	public class EmailSubmissionException( string tMessage, HttpStatusCode? tStatusCode = null, Exception tInnerException = null ) : Exception( tMessage, tInnerException )
	{
		public HttpStatusCode? StatusCode { get; } = tStatusCode;
	}
}
