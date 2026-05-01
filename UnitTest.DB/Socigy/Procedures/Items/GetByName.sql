-- @returns: UnitTest.DB.TestItem
-- @param name: string
SELECT "id", "name", "priority", "created_at" FROM "test_items" WHERE "name" = @name
