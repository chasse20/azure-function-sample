using System;

namespace DataService.DocuSign
{
	public class EnvelopeEvent
	{
		public string Event { get; set; }
		public DateTime GeneratedDateTime { get; set; }
		public EnvelopeEventData Data { get; set; }
	}
}
