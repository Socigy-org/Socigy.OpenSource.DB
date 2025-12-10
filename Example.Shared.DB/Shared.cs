using Socigy.OpenSource.DB.Attributes;

namespace Example.Shared.DB
{
    [Table("shared")]
    public partial class Shared
    {
        [PrimaryKey]
        public Guid Id { get; set; }
    }
}
