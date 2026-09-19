using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;

namespace DataService.SQL
{
	[Table( nameof( File ) )]
	public class File
	{
		[Key]
		public int FileId { get; set; }
		[JsonIgnore]
		public EnvelopePreview EnvelopePreview { get; set; }
		[JsonIgnore]
		public ICollection<EnvelopeFile> EnvelopeFiles { get; set; }
		public DateTime CreatedDate { get; set; }
		public bool IsActive { get; set; }
		[Required]
		[MaxLength( 255 )]
		public string Name { get; set; }
		[Required]
		[MaxLength( 511 )]
		public string AzureBlobURI { get; set; }
	}
}
