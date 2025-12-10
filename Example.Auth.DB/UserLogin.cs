using Socigy.OpenSource.DB.Attributes;

namespace Example.Auth.DB
{
    [Table("user_login")]
    public partial class UserLogin
    {
        [PrimaryKey, Default]
        public Guid Id { get; set; }

        public string Username { get; set; } = "Tvoje máma";

        public string? PasswordHash { get; set; }
    }
}
