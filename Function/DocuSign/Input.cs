using DataService.Function.Envelope;
using System;

namespace DataService.Function.DocuSign
{
	public class Input
	{
		public int EnvelopeId { get; set; }
		public EnvelopeStatus NewStatus { get; set; }
		public DateTime Date { get; set; }
	}
}
