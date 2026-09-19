namespace DataService.Function.Envelope
{
	public enum EnvelopeStatus : byte
	{
		Draft,
		Sending,
		Sent,
		Signed,
		Voiding,
		Voided
	}
}
