using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;

namespace DataService.SQL
{
	[Table( nameof( EnvelopePreview ) )]
	public class EnvelopePreview
	{
		[Key]
		[ForeignKey( nameof( Envelope ) )]
		public int EnvelopeId { get; set; }
		[Required]
		[JsonIgnore]
		public Envelope Envelope { get; set; }
		[ForeignKey( nameof( File ) )]
		public int? FileId { get; set; }
		public File File { get; set; }
		[MaxLength( 255 )]
		public string DocusignEnvelopeId { get; set; }
	}
}
