using Socigy.OpenSource.DB.Attributes;

namespace Example.User.DB
{
    [Table("user_login")]
    public partial class UserInfo
    {
        [PrimaryKey]
        public Guid Id { get; set; }

        [Unique]
        public string Email { get; set; }

        public string Username { get; set; }

        public string FirstName { get; set; }
        public string LastName { get; set; }
    }
}
