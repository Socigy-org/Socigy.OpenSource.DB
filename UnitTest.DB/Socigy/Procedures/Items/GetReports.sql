-- @returns: UnitTest.DB.ItemReport
-- @param name: string
SELECT {{TestItem.Name}} AS "Name", {{TestItem.Priority}} AS "Priority", 200::smallint AS "Rank", 200::smallint AS "Level", 40000::integer AS "WideShort", 3000000000::bigint AS "WideInt", 10000000000::numeric AS "WideLong" FROM {{TestItem}} WHERE {{TestItem.Name}} = @name
