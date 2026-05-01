-- @returns: Example.Auth.DB.UserCourse
-- @param userId: System.Guid
SELECT "user_id", "course_id", "registered_at"
FROM "user_course"
WHERE "user_id" = @userId
