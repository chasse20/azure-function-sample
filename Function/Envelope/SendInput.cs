using System;

namespace DataService.Function.Envelope
{
	public class SendInput
	{
		public int EnvelopeId { get; set; }
		public DateTime Date { get; set; }
		public string RecipientName { get; set; }
		public string RecipientEmail { get; set; }
	}
}
