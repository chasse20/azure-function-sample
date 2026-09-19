using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;

namespace DataService.Function
{
	public sealed record BlobFile( BlobClient Client, BlobContentInfo ContentInfo );
}
