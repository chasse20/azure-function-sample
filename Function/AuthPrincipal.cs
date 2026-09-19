using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace DataService.Function
{
	public class AuthPrincipal
	{
		[JsonPropertyName( "auth_typ" )]
		public string AuthenticationType { get; set; }
		[JsonPropertyName( "role_typ" )]
		public string RoleClaimType { get; set; }
		[JsonPropertyName( "claims" )]
		public List<AuthPrincipalClaim> Claims { get; set; }
	}
}
