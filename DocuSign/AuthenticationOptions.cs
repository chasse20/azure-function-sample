using System;
using System.Security.Cryptography;

namespace DataService.DocuSign
{
	public class AuthenticationOptions : IDisposable
	{
		public string ClientURI { get; }
		public string AuthServer { get; }
		public string IntegrationKey { get; }
		public string UserID { get; }
		public string AccountID { get; }
		public byte[] PrivateKey { get; }
		public int TokenExpirationHours { get; }
		public int MaxTokenAttempts { get; }

		public AuthenticationOptions( string tClientURI, string tAuthServer, string tIntegrationKey, string tUserID, string tAccountID, byte[] tPrivateKey, int tTokenExpirationHours, int tMaxTokenAttempts )
		{
			ArgumentException.ThrowIfNullOrWhiteSpace( tClientURI );
			ArgumentException.ThrowIfNullOrWhiteSpace( tAuthServer );
			ArgumentException.ThrowIfNullOrWhiteSpace( tIntegrationKey );
			ArgumentException.ThrowIfNullOrWhiteSpace( tUserID );
			ArgumentException.ThrowIfNullOrWhiteSpace( tAccountID );
			ArgumentNullException.ThrowIfNull( tPrivateKey );

			if ( tPrivateKey.Length == 0 )
			{
				throw new ArgumentException( "The DocuSign private key cannot be empty.", nameof( tPrivateKey ) );
			}

			if ( tTokenExpirationHours <= 0 )
			{
				throw new ArgumentOutOfRangeException( nameof( tTokenExpirationHours ), "The token expiration must be greater than zero." );
			}

			if ( tMaxTokenAttempts <= 0 )
			{
				throw new ArgumentOutOfRangeException( nameof( tMaxTokenAttempts ), "The maximum token attempts must be greater than zero." );
			}

			ClientURI = tClientURI;
			AuthServer = tAuthServer;
			IntegrationKey = tIntegrationKey;
			UserID = tUserID;
			AccountID = tAccountID;
			PrivateKey = tPrivateKey;
			TokenExpirationHours = tTokenExpirationHours;
			MaxTokenAttempts = tMaxTokenAttempts;
		}

		public virtual void Dispose()
		{
			CryptographicOperations.ZeroMemory( PrivateKey );
			GC.SuppressFinalize( this );
		}
	}
}
