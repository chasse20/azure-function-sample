namespace DataService.Function.Envelope
{
	public class SendRequest
	{
		public int SenderId { get; set; }
		public string Name { get; set; }
		public string RecipientName { get; set; }
		public string RecipientEmail { get; set; }
	}
}
