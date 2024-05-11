using System;
using System.Data.SQLite;
using System.IO;
using System.Data;

namespace IncomeTaxWebAPI
{
    public static class DBManager
    {
        public static SQLiteConnection CreateConnection(string dbName)
        {
            SQLiteConnection sqlite_conn;
            if (File.Exists(@"C:\EInvoice\"+ dbName + ".db") == false)
            {
                //sqlite_conn = new SQLiteConnection(@"Data Source=C:\EInvoice\"+ dbName + ".db;Version=3;");
                sqlite_conn = new SQLiteConnection(@"Data Source=C:\EInvoice\" + dbName + ".db;");
                sqlite_conn.Open();
                //sqlite_conn.ChangePassword("1509Mariam@22Nagy@08Tawfik@1995Toma1988");

                CreateDBFile(sqlite_conn);
                sqlite_conn.Close();
            }
            else
            {
                //sqlite_conn = new SQLiteConnection(@"Data Source=C:\EInvoice\"+ dbName + ".db;Version=3;Password=1509Mariam@22Nagy@08Tawfik@1995Toma1988;");
                sqlite_conn = new SQLiteConnection(@"Data Source=C:\EInvoice\" + dbName + ".db;");
            }
            try
            {
                sqlite_conn.Open();
            }
            catch (Exception)
            {
                if (sqlite_conn.State == ConnectionState.Open)
                {
                    sqlite_conn.Close();
                }
                return null;
            }
            return sqlite_conn;
        }
        public static void CreateDBFile(SQLiteConnection connection)
        {
            string createSQL = @"SELECT 1;
PRAGMA foreign_keys=OFF;
CREATE TABLE [tbDelivery] (
  [Id] INTEGER PRIMARY KEY AUTOINCREMENT NOT NULL
, [Delivery_Id] text NULL
, [Delivery_Value] text NULL
);";
            try
            {
                SQLiteCommand cmd = new SQLiteCommand(connection);
                cmd.CommandText = createSQL;
                cmd.ExecuteNonQuery();
            }
            catch (Exception e)
            {
                throw new Exception(e.Message);
            }
            finally
            {
                if (connection.State == ConnectionState.Open)
                {
                    connection.Close();
                }
            }
        }
        public static void AddDelivery(Delivery delivery,string dbName)
        {
            SQLiteConnection connection = CreateConnection(dbName);
            if (connection != null)
            {
                string createSQL = @"SELECT 1;
PRAGMA foreign_keys=OFF;
BEGIN TRANSACTION;
INSERT INTO [tbDelivery] ([Delivery_Id],[Delivery_Value]) VALUES (@Delivery_Id,@Delivery_Value);
COMMIT;";
                try
                {
                    SQLiteCommand cmd = new SQLiteCommand(connection);
                    cmd.CommandText = createSQL;
                    cmd.Parameters.AddWithValue("@Delivery_Id", delivery.DeliveryId);
                    cmd.Parameters.AddWithValue("@Delivery_Value", delivery.DeliveryToJson());
                    cmd.ExecuteNonQuery();
                }
                catch (Exception e)
                {
                    throw new Exception(e.Message);
                }
                finally
                {
                    if (connection != null)
                    {
                        if (connection.State == ConnectionState.Open)
                        {
                            connection.Close();
                        }
                    }
                }

            }
        }

    }
}