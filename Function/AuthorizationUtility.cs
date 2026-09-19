using Microsoft.Azure.Functions.Worker.Http;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;

namespace DataService.Function
{
	public static class AuthorizationUtility
	{
		public static HttpResponseData GetAuthorizationResponse( HttpRequestData tRequest, string tRequiredRole )
		{
			if ( !tRequest.Headers.TryGetValues( "X-MS-CLIENT-PRINCIPAL", out IEnumerable<string> tempHeaderValues ) )
			{
				return tRequest.CreateResponse( HttpStatusCode.Unauthorized );
			}

			string tempHeader = tempHeaderValues.FirstOrDefault();

			if ( string.IsNullOrWhiteSpace( tempHeader ) )
			{
				return tRequest.CreateResponse( HttpStatusCode.Unauthorized );
			}

			try
			{
				byte[] tempPrincipalBytes = Convert.FromBase64String( tempHeader );
				AuthPrincipal tempPrincipal = JsonSerializer.Deserialize<AuthPrincipal>( Encoding.UTF8.GetString( tempPrincipalBytes ) );

				if ( tempPrincipal == null || string.IsNullOrWhiteSpace( tempPrincipal.AuthenticationType ) )
				{
					return tRequest.CreateResponse( HttpStatusCode.Unauthorized );
				}

				string tempRoleClaimType = string.IsNullOrWhiteSpace( tempPrincipal.RoleClaimType ) ? "roles" : tempPrincipal.RoleClaimType;

				if
				(
					tempPrincipal.Claims?.Any
					(
						x =>
							(
								string.Equals( x.Type, tempRoleClaimType, StringComparison.OrdinalIgnoreCase )
								|| string.Equals( x.Type, "roles", StringComparison.OrdinalIgnoreCase )
								|| string.Equals( x.Type, "http://schemas.microsoft.com/ws/2008/06/identity/claims/role", StringComparison.OrdinalIgnoreCase )
							)
							&& string.Equals( x.Value, tRequiredRole, StringComparison.Ordinal )
					) == true
				)
				{
					return null;
				}

				return tRequest.CreateResponse( HttpStatusCode.Forbidden );
			}
			catch ( FormatException )
			{
				return tRequest.CreateResponse( HttpStatusCode.Unauthorized );
			}
			catch ( JsonException )
			{
				return tRequest.CreateResponse( HttpStatusCode.Unauthorized );
			}
		}
	}
}
