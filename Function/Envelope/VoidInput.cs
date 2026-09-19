using System;

namespace DataService.Function.Envelope
{
	public class VoidInput
	{
		public int EnvelopeId { get; set; }
		public bool IsVoided { get; set; }
		public DateTime Date { get; set; }
	}
}
