using System.Text.Json.Serialization;

namespace DataService.Function
{
	public class AuthPrincipalClaim
	{
		[JsonPropertyName( "typ" )]
		public string Type { get; set; }
		[JsonPropertyName( "val" )]
		public string Value { get; set; }
	}
}
