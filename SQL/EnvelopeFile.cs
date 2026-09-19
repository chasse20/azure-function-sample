using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;

namespace DataService.SQL
{
	[Table( nameof( EnvelopeFile ) )]
	public class EnvelopeFile
	{
		[Key]
		[ForeignKey( nameof( Envelope ) )]
		public int EnvelopeId { get; set; }
		[Required]
		[JsonIgnore]
		public Envelope Envelope { get; set; }
		[Key]
		[ForeignKey( nameof( File ) )]
		public int FileId { get; set; }
		[Required]
		[JsonIgnore]
		public File File { get; set; }
		public int Sequence { get; set; }
	}
}
