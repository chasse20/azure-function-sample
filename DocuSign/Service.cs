using DocuSign.eSign.Api;
using DocuSign.eSign.Client;
using DocuSign.eSign.Client.Auth;
using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using DocuSignModel = DocuSign.eSign.Model;

namespace DataService.DocuSign
{
	public class Service( AuthenticationOptions tOptions, ILogger<Service> tLogger ) : IService, IDisposable
	{
		protected const int TOKEN_EXPIRATION_BUFFER_SECONDS = 300;
		protected const string COMBINED_DOCUMENT_ID = "combined";
		protected const string RECYCLE_BIN_FOLDER_ID = "recyclebin";
		protected const string VOIDED_STATUS = "voided";
		protected const string EDIT_LOCK_TYPE = "edit";

		protected readonly AuthenticationOptions _options = tOptions;
		protected readonly ILogger<Service> _logger = tLogger;
		protected readonly SemaphoreSlim _tokenLock = new( 1, 1 );

		protected DocuSignClient _client;
		protected OAuth.OAuthToken _token;
		protected DateTimeOffset _tokenExpirationUtc;

		public virtual async Task<DocuSignModel.Recipients> GetTemplateRecipientsAsync( string tTemplateId, CancellationToken tCancel )
		{
			ArgumentException.ThrowIfNullOrWhiteSpace( tTemplateId );
			tCancel.ThrowIfCancellationRequested();

			TemplatesApi tempAPI = await GetTemplatesAPIAsync( tCancel );

			tCancel.ThrowIfCancellationRequested();

			try
			{
				DocuSignModel.EnvelopeTemplate tempTemplate = await tempAPI.GetAsync( _options.AccountID, tTemplateId );

				return tempTemplate?.Recipients;
			}
			catch ( OperationCanceledException ) when ( tCancel.IsCancellationRequested )
			{
				throw;
			}
			catch ( OperationCanceledException tException )
			{
				throw new TimeoutException( "DocuSign timed out while getting template recipients.", tException );
			}
		}

		public virtual async Task<DocuSignModel.EnvelopeSummary> CreateEnvelopeAsync( DocuSignModel.EnvelopeDefinition tEnvelopeDefinition, CancellationToken tCancel )
		{
			ArgumentNullException.ThrowIfNull( tEnvelopeDefinition );
			tCancel.ThrowIfCancellationRequested();

			EnvelopesApi tempAPI = await GetEnvelopesAPIAsync( tCancel );

			tCancel.ThrowIfCancellationRequested();

			try
			{
				DocuSignModel.EnvelopeSummary tempSummary = await tempAPI.CreateEnvelopeAsync( _options.AccountID, tEnvelopeDefinition );

				if ( string.IsNullOrWhiteSpace( tempSummary?.EnvelopeId ) )
				{
					throw new InvalidOperationException( "DocuSign created an envelope without returning an envelope ID." );
				}

				return tempSummary;
			}
			catch ( OperationCanceledException ) when ( tCancel.IsCancellationRequested )
			{
				throw;
			}
			catch ( OperationCanceledException tException )
			{
				throw new TimeoutException( "DocuSign timed out while creating the envelope.", tException );
			}
		}

		public virtual async Task<DocuSignModel.LockInformation> LockEnvelopeAsync( string tDocuSignEnvelopeId, string tLockedByApp, int tLockDurationInSeconds, CancellationToken tCancel )
		{
			ArgumentException.ThrowIfNullOrWhiteSpace( tDocuSignEnvelopeId );
			ArgumentException.ThrowIfNullOrWhiteSpace( tLockedByApp );

			if ( tLockDurationInSeconds <= 0 )
			{
				throw new ArgumentOutOfRangeException( nameof( tLockDurationInSeconds ), "The lock duration must be greater than zero." );
			}

			tCancel.ThrowIfCancellationRequested();

			EnvelopesApi tempAPI = await GetEnvelopesAPIAsync( tCancel );

			tCancel.ThrowIfCancellationRequested();

			try
			{
				return await tempAPI.CreateLockAsync
				(
					_options.AccountID,
					tDocuSignEnvelopeId,
					new DocuSignModel.LockRequest
					{
						LockedByApp = tLockedByApp,
						LockDurationInSeconds = tLockDurationInSeconds.ToString(),
						LockType = EDIT_LOCK_TYPE
					}
				);
			}
			catch ( OperationCanceledException ) when ( tCancel.IsCancellationRequested )
			{
				throw;
			}
			catch ( OperationCanceledException tException )
			{
				throw new TimeoutException( "DocuSign timed out while locking the envelope.", tException );
			}
		}

		public virtual async Task<DocuSignModel.EnvelopeUpdateSummary> VoidEnvelopeAsync( string tDocuSignEnvelopeId, string tReason, CancellationToken tCancel )
		{
			ArgumentException.ThrowIfNullOrWhiteSpace( tDocuSignEnvelopeId );
			ArgumentException.ThrowIfNullOrWhiteSpace( tReason );
			tCancel.ThrowIfCancellationRequested();

			EnvelopesApi tempAPI = await GetEnvelopesAPIAsync( tCancel );

			tCancel.ThrowIfCancellationRequested();

			try
			{
				DocuSignModel.Envelope tempEnvelope = new() { Status = VOIDED_STATUS, VoidedReason = tReason };
				return await tempAPI.UpdateAsync( _options.AccountID, tDocuSignEnvelopeId, tempEnvelope );
			}
			catch ( OperationCanceledException ) when ( tCancel.IsCancellationRequested )
			{
				throw;
			}
			catch ( OperationCanceledException tException )
			{
				throw new TimeoutException( "DocuSign timed out while voiding the envelope.", tException );
			}
		}

		public virtual async Task<Stream> GetCombinedDocumentAsync( string tDocuSignEnvelopeId, CancellationToken tCancel )
		{
			ArgumentException.ThrowIfNullOrWhiteSpace( tDocuSignEnvelopeId );
			tCancel.ThrowIfCancellationRequested();

			EnvelopesApi tempAPI = await GetEnvelopesAPIAsync( tCancel );

			tCancel.ThrowIfCancellationRequested();

			try
			{
				Stream tempStream = await tempAPI.GetDocumentAsync( _options.AccountID, tDocuSignEnvelopeId, COMBINED_DOCUMENT_ID );

				return tempStream ?? throw new InvalidOperationException( "DocuSign returned an empty combined-document response." );
			}
			catch ( OperationCanceledException ) when ( tCancel.IsCancellationRequested )
			{
				throw;
			}
			catch ( OperationCanceledException tException )
			{
				throw new TimeoutException( "DocuSign timed out while downloading the combined document.", tException );
			}
		}

		public virtual async Task<DocuSignModel.FoldersResponse> MoveEnvelopeToRecycleBinAsync( string tDocuSignEnvelopeId, CancellationToken tCancel )
		{
			ArgumentException.ThrowIfNullOrWhiteSpace( tDocuSignEnvelopeId );
			tCancel.ThrowIfCancellationRequested();

			FoldersApi tempAPI = await GetFoldersAPIAsync( tCancel );

			tCancel.ThrowIfCancellationRequested();

			try
			{
				DocuSignModel.FoldersRequest tempFolderModel = new() { EnvelopeIds = [ tDocuSignEnvelopeId ] };
				return await tempAPI.MoveEnvelopesAsync( _options.AccountID, RECYCLE_BIN_FOLDER_ID, tempFolderModel );
			}
			catch ( OperationCanceledException ) when ( tCancel.IsCancellationRequested )
			{
				throw;
			}
			catch ( OperationCanceledException tException )
			{
				throw new TimeoutException( "DocuSign timed out while moving the envelope to the recycle bin.", tException );
			}
		}

		protected virtual async Task<TemplatesApi> GetTemplatesAPIAsync( CancellationToken tCancel ) => new( await GetAuthenticatedClientAsync( tCancel ) );

		protected virtual async Task<EnvelopesApi> GetEnvelopesAPIAsync( CancellationToken tCancel ) => new( await GetAuthenticatedClientAsync( tCancel ) );

		protected virtual async Task<FoldersApi> GetFoldersAPIAsync( CancellationToken tCancel ) => new( await GetAuthenticatedClientAsync( tCancel ) );

		protected virtual async Task<DocuSignClient> GetAuthenticatedClientAsync( CancellationToken tCancel )
		{
			tCancel.ThrowIfCancellationRequested();

			if ( IsCurrentTokenValid() )
			{
				return _client;
			}

			await _tokenLock.WaitAsync( tCancel );

			try
			{
				if ( IsCurrentTokenValid() )
				{
					return _client;
				}

				DocuSignClient tempClient = new( _options.ClientURI );
				OAuth.OAuthToken tempToken = await GetTokenAsync( tempClient, tCancel );

				if ( string.IsNullOrWhiteSpace( tempToken?.access_token ) )
				{
					throw new InvalidOperationException( "DocuSign returned an empty access token." );
				}

				int tempExpiresInSeconds = tempToken.expires_in ?? 3600;
				int tempExpirationBufferSeconds = Math.Min( TOKEN_EXPIRATION_BUFFER_SECONDS, Math.Max( 1, tempExpiresInSeconds / 10 ) );
				int tempAdjustedExpirationSeconds = Math.Max( 1, tempExpiresInSeconds - tempExpirationBufferSeconds );

				_client = tempClient;
				_token = tempToken;
				_tokenExpirationUtc = DateTimeOffset.UtcNow.AddSeconds( tempAdjustedExpirationSeconds );

				return _client;
			}
			finally
			{
				_tokenLock.Release();
			}
		}

		protected virtual async Task<OAuth.OAuthToken> GetTokenAsync( DocuSignClient tClient, CancellationToken tCancel )
		{
			for ( int i = 1; i <= _options.MaxTokenAttempts; ++i )
			{
				tCancel.ThrowIfCancellationRequested();

				try
				{
					return await tClient.RequestJWTUserTokenAsync
					(
						_options.IntegrationKey,
						_options.UserID,
						_options.AuthServer,
						_options.PrivateKey,
						_options.TokenExpirationHours,
						[ "signature", "impersonation" ],
						tCancel
					);
				}
				catch ( OperationCanceledException ) when ( tCancel.IsCancellationRequested )
				{
					throw;
				}
				catch ( Exception tException ) when ( IsTransientTokenException( tException ) )
				{
					if ( i >= _options.MaxTokenAttempts )
					{
						if ( tException is OperationCanceledException )
						{
							throw new TimeoutException( "DocuSign JWT token acquisition timed out.", tException );
						}

						throw;
					}

					_logger.LogWarning( tException, "Failed to retrieve the DocuSign JWT token. Attempt: {Attempt}. MaxAttempts: {MaxAttempts}", i, _options.MaxTokenAttempts );

					await Task.Delay( TimeSpan.FromSeconds( Math.Min( 30, Math.Pow( 2, i - 1 ) + Random.Shared.NextDouble() ) ), tCancel );
				}
			}

			throw new InvalidOperationException( "The DocuSign JWT token loop completed unexpectedly." );
		}

		protected virtual bool IsCurrentTokenValid()
		{
			return _client != null && _token != null && !string.IsNullOrWhiteSpace( _token.access_token ) && DateTimeOffset.UtcNow < _tokenExpirationUtc;
		}

		protected static bool IsTransientTokenException( Exception tException )
		{
			return tException switch
			{
				ApiException tempException => IsTransientStatusCode( tempException.ErrorCode ),
				HttpRequestException tempException when tempException.StatusCode.HasValue => IsTransientStatusCode( (int)tempException.StatusCode.Value ),
				HttpRequestException => true,
				TimeoutException => true,
				OperationCanceledException => true,
				_ => false
			};
		}

		protected static bool IsTransientStatusCode( int tStatusCode )
		{
			return tStatusCode == (int)HttpStatusCode.RequestTimeout
				|| tStatusCode == 429 // Too Many Requests
				|| tStatusCode == (int)HttpStatusCode.InternalServerError
				|| tStatusCode == (int)HttpStatusCode.BadGateway
				|| tStatusCode == (int)HttpStatusCode.ServiceUnavailable
				|| tStatusCode == (int)HttpStatusCode.GatewayTimeout;
		}

		public virtual void Dispose()
		{
			_tokenLock.Dispose();
			GC.SuppressFinalize( this );
		}
	}
}
