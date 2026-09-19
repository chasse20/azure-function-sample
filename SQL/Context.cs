using Microsoft.EntityFrameworkCore;

namespace DataService.SQL
{
	public class Context( DbContextOptions<Context> tOptions ) : DbContext( tOptions )
	{
		public DbSet<Envelope> Envelope { get; set; }
		public DbSet<EnvelopeFile> EnvelopeFile { get; set; }
		public DbSet<EnvelopePreview> EnvelopePreview { get; set; }
		public DbSet<File> File { get; set; }

		protected override void OnModelCreating( ModelBuilder tBuilder )
		{
			tBuilder.Entity<EnvelopeFile>().HasKey( x => new { x.EnvelopeId, x.FileId } );
		}
	}
}
