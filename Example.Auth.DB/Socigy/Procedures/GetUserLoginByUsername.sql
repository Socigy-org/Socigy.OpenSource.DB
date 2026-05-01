-- @returns: Example.Auth.DB.UserLogin
-- @param username: string
SELECT "id", "username", "password_hash"
FROM "user_login"
WHERE "username" = @username
