using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace DataService.SQL
{
	[Table( nameof( Envelope ) )]
	public class Envelope
	{
		[Key]
		public int EnvelopeId { get; set; }
		public ICollection<EnvelopeFile> EnvelopeFiles { get; set; }
		public EnvelopePreview EnvelopePreview { get; set; }
		public int? SenderId { get; set; }
		public int? VoiderId { get; set; }
		public DateTime? SentDate { get; set; }
		public DateTime? VoidedDate { get; set; }
		public DateTime? SignedDate { get; set; }
		[Required]
		[MaxLength( 255 )]
		public string Name { get; set; }
		[MaxLength( 255 )]
		public string DocusignEnvelopeId { get; set; }
	}
}
