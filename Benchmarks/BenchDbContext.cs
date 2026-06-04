using Microsoft.EntityFrameworkCore;

namespace Benchmarks;

/// <summary>EF Core context mapping <see cref="BenchUser"/> to the shared <c>bench_users</c> table.</summary>
public sealed class BenchDbContext : DbContext
{
    private readonly string _connectionString;

    public BenchDbContext(string connectionString) => _connectionString = connectionString;

    public DbSet<BenchUser> Users => Set<BenchUser>();
    public DbSet<BenchWrite> Writes => Set<BenchWrite>();

    protected override void OnConfiguring(DbContextOptionsBuilder options)
        => options.UseNpgsql(_connectionString);

    protected override void OnModelCreating(ModelBuilder builder)
    {
        var user = builder.Entity<BenchUser>();
        user.ToTable("bench_users");
        user.HasKey(x => x.Id);
        user.Property(x => x.Id).HasColumnName("id");
        user.Property(x => x.Name).HasColumnName("name");
        user.Property(x => x.Age).HasColumnName("age");
        user.Property(x => x.CreatedAt).HasColumnName("created_at");

        var write = builder.Entity<BenchWrite>();
        write.ToTable("bench_writes");
        write.HasKey(x => x.Id);
        write.Property(x => x.Id).HasColumnName("id");
        write.Property(x => x.Name).HasColumnName("name");
        write.Property(x => x.Age).HasColumnName("age");
    }
}
