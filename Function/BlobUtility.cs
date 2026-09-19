using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace DataService.Function
{
	public static class BlobUtility
	{
		public static async Task<BlobFile> UploadFileBlobAsync( this BlobContainerClient tBlobContainer, Stream tStream, string tBlobName, Dictionary<string, string> tMetaData, CancellationToken tCancel )
		{
			tCancel.ThrowIfCancellationRequested();

			if ( !tStream.CanRead )
			{
				throw new InvalidOperationException( "The file stream is not readable." );
			}

			if ( tStream.CanSeek )
			{
				tStream.Position = 0;
			}

			BlobClient tempBlobClient = tBlobContainer.GetBlobClient( tBlobName );
			BlobUploadOptions tempOptions = new()
			{
				Metadata = new Dictionary<string, string>( tMetaData ),
				Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All }
			};

			Response<BlobContentInfo> tempResponse = await tempBlobClient.UploadAsync( tStream, tempOptions, tCancel );

			return new( tempBlobClient, tempResponse.Value );
		}

		public static async Task<bool> DeleteFileBlobAsync( this BlobContainerClient tBlobContainer, string tBlobName, CancellationToken tCancel )
		{
			tCancel.ThrowIfCancellationRequested();

			BlobClient tempBlobClient = tBlobContainer.GetBlobClient( tBlobName );
			Response<bool> tempResponse = await tempBlobClient.DeleteIfExistsAsync( DeleteSnapshotsOption.IncludeSnapshots, cancellationToken: tCancel );

			return tempResponse.Value;
		}

		public static async Task<ETag> CopyFileBlobAsync( this BlobContainerClient tBlobContainer, string tFromBlobName, string tToBlobName, Dictionary<string, string> tNewMetaData, CancellationToken tCancel )
		{
			if ( string.Equals( tFromBlobName, tToBlobName, StringComparison.Ordinal ) )
			{
				throw new ArgumentException( "The source and destination blob names must be different." );
			}

			tCancel.ThrowIfCancellationRequested();

			BlobClient tempFromBlobClient = tBlobContainer.GetBlobClient( tFromBlobName );
			BlobClient tempToBlobClient = tBlobContainer.GetBlobClient( tToBlobName );
			BlobProperties tempProperties = ( await tempFromBlobClient.GetPropertiesAsync( cancellationToken: tCancel ) ).Value;
			Dictionary<string, string> tempMetaData = new( tempProperties.Metadata, StringComparer.OrdinalIgnoreCase );

			foreach ( KeyValuePair<string, string> tempKVP in tNewMetaData )
			{
				tempMetaData[ tempKVP.Key ] = tempKVP.Value;
			}

			BlobCopyFromUriOptions tempOptions = new()
			{
				Metadata = tempMetaData,
				SourceConditions = new BlobRequestConditions { IfMatch = tempProperties.ETag },
				DestinationConditions = new BlobRequestConditions { IfNoneMatch = ETag.All }
			};

			await tempToBlobClient.SyncCopyFromUriAsync( tempFromBlobClient.Uri, tempOptions, tCancel );

			return tempProperties.ETag;
		}

		public static async Task DeleteFileBlobAfterCommitAsync( this BlobContainerClient tBlobContainer, string tBlobName, ILogger tLogger, CancellationToken tCancel, ETag? tETag = null )
		{
			if ( string.IsNullOrWhiteSpace( tBlobName ) )
			{
				return;
			}

			try
			{
				BlobRequestConditions tempConditions = tETag.HasValue ? new() { IfMatch = tETag.Value } : null;
				await tBlobContainer.GetBlobClient( tBlobName ).DeleteIfExistsAsync( DeleteSnapshotsOption.IncludeSnapshots, tempConditions, tCancel );
			}
			catch ( OperationCanceledException ) when ( tCancel.IsCancellationRequested )
			{
				throw;
			}
			catch ( Exception tException )
			{
				tLogger.LogWarning( tException, "Post-commit blob cleanup failed for {BlobName}.", tBlobName );
			}
		}
	}
}
