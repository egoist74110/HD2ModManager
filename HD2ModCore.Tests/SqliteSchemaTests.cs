using System.Globalization;
using HD2ModCore.Infrastructure;
using HD2ModCore.Infrastructure.Sqlite;
using Microsoft.Data.Sqlite;

namespace HD2ModCore.Tests;

// 作用：验证索引库结构的迁移动作本身——尤其是"退役的列要真的从旧库里消失"。
// Purpose: Verifies the index schema migrations themselves, especially that retired columns
// actually disappear from an existing database instead of lingering next to a fresh one.
public sealed class SqliteSchemaTests
{
	// 权威格子已经改成读取时 JOIN 注册表（见 UnitPartFactQuery），部位表里那两列副本退役。
	// 只把 CREATE TABLE 改掉是不够的：`IF NOT EXISTS` 不会动已存在的表，旧库会永远多两列，
	// 于是"新库和旧库结构不同"这件事会一直存在，而没人知道哪些列还算数。
	[Fact]
	public async Task EnsureCreatedAsync_DropsTheRetiredAuthorityCellColumns()
	{
		var root = Path.Combine(Path.GetTempPath(), "hd2-schema-tests", Guid.NewGuid().ToString("N"));
		var paths = new StoragePaths(Path.Combine(root, "app"));
		Directory.CreateDirectory(paths.IndexDirectory);
		try
		{
			await using (var connection = new SqliteConnection($"Data Source={paths.DbPath};Pooling=False"))
			{
				await connection.OpenAsync();
				await SqliteSchema.EnsureCreatedAsync(connection, CancellationToken.None);
				// 把两列加回去，模拟一个还没迁移过的旧库。
				await ExecuteAsync(connection, "ALTER TABLE game_data_unit_parts ADD COLUMN authority_slot INTEGER NOT NULL DEFAULT -1;");
				await ExecuteAsync(connection, "ALTER TABLE game_data_unit_parts ADD COLUMN authority_piece_type INTEGER NOT NULL DEFAULT -1;");
				Assert.Equal(2L, await CountAuthorityColumnsAsync(connection));
			}

			await using (var connection = new SqliteConnection($"Data Source={paths.DbPath};Pooling=False"))
			{
				await connection.OpenAsync();
				await SqliteSchema.EnsureCreatedAsync(connection, CancellationToken.None);
				Assert.Equal(0L, await CountAuthorityColumnsAsync(connection));
				Assert.Equal(
					SqliteSchema.SchemaVersion.ToString(CultureInfo.InvariantCulture),
					await SqliteSchema.GetMetaAsync(connection, "schema_version", CancellationToken.None));
			}
		}
		finally
		{
			try { Directory.Delete(root, recursive: true); } catch { }
		}
	}

	[Fact]
	public async Task EnsureCreatedAsync_RecordsAuthoritySchemaOnFreshDatabase_AndPreservesAuthorityRowsOnReopen()
	{
		var root = Path.Combine(Path.GetTempPath(), "hd2-schema-tests", Guid.NewGuid().ToString("N"));
		var paths = new StoragePaths(Path.Combine(root, "app"));
		Directory.CreateDirectory(paths.IndexDirectory);
		try
		{
			await using (var connection = new SqliteConnection($"Data Source={paths.DbPath};Pooling=False"))
			{
				await connection.OpenAsync();
				await SqliteSchema.EnsureCreatedAsync(connection, CancellationToken.None);
				Assert.Equal("2", await SqliteSchema.GetMetaAsync(connection, "authority_pieces_schema", CancellationToken.None));

				await ExecuteAsync(connection, """
					INSERT INTO game_data_authority_kits(item_id, dlc_id, set_id, kit_kind, archive_id, display_name)
					VALUES(1, 0, 1, 0, 'aaaaaaaaaaaaaaaa', 'B-01 Tactical');
					""");
			}

			// 模拟建完基础索引后，高级索引或目录服务再次调用 EnsureCreatedAsync：不得把刚写好的权威表 DROP 掉。
			await using (var connection = new SqliteConnection($"Data Source={paths.DbPath};Pooling=False"))
			{
				await connection.OpenAsync();
				await SqliteSchema.EnsureCreatedAsync(connection, CancellationToken.None);

				await using var count = connection.CreateCommand();
				count.CommandText = "SELECT count(*) FROM game_data_authority_kits;";
				Assert.Equal(1L, Convert.ToInt64(await count.ExecuteScalarAsync(), CultureInfo.InvariantCulture));
			}
		}
		finally
		{
			try { Directory.Delete(root, recursive: true); } catch { }
		}
	}

	private static async Task<long> CountAuthorityColumnsAsync(SqliteConnection connection)
	{
		await using var command = connection.CreateCommand();
		command.CommandText = "SELECT count(*) FROM pragma_table_info('game_data_unit_parts') WHERE name LIKE 'authority%';";
		return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
	}

	private static async Task ExecuteAsync(SqliteConnection connection, string sql)
	{
		await using var command = connection.CreateCommand();
		command.CommandText = sql;
		await command.ExecuteNonQueryAsync();
	}
}
