﻿using DatabaseSchemaReader.DataSchema;
using System;
using System.Linq;
using System.Text;

namespace DatabaseSchemaReader.SqlGen.PostgreSql
{
    internal class TableGenerator : TableGeneratorBase
    {
        private bool _hasBit;
        protected DataTypeWriter DataTypeWriter;

        public TableGenerator(DatabaseTable table)
            : base(table)
        {
            DataTypeWriter = new DataTypeWriter();
        }

        public override string Write()
        {
            string desc = null;
            if (!string.IsNullOrEmpty(Table.Description))
            {
                desc = AddTableDescription();
            }
            if (Table.Columns.Any(c => !string.IsNullOrEmpty(c.Description)))
            {
                desc = desc + AddColumnDescriptions();
            }
            return base.Write() + desc;
        }

        protected virtual string AddColumnDescriptions()
        {
            var sb = new StringBuilder();
            var formatProvider = SqlFormatProvider();
            var tableName = SchemaTableName(Table);
            foreach (var column in Table.Columns.Where(c => !string.IsNullOrEmpty(c.Description)))
            {
                sb.Append("COMMENT ON COLUMN ");
                sb.Append(tableName + "." + (EscapeNames ? formatProvider.Escape(column.Name) : column.Name));
                sb.Append(" IS '");
                sb.Append(column.Description);
                sb.AppendLine("'" + formatProvider.LineEnding());
            }
            return sb.ToString();
        }

        protected virtual string AddTableDescription()
        {
            var formatProvider = SqlFormatProvider();
            var sb = new StringBuilder();
            sb.Append("COMMENT ON TABLE ");
            sb.Append(SchemaTableName(Table));
            sb.Append(" IS '");
            sb.Append(Table.Description);
            sb.AppendLine("'" + formatProvider.LineEnding());
            return sb.ToString();
        }

        protected override string ConstraintWriter()
        {
            var sb = new StringBuilder();
            var constraintWriter = CreateConstraintWriter();

            if (Table.PrimaryKey != null)
            {
                sb.AppendLine(constraintWriter.WritePrimaryKey());
            }

            sb.AppendLine(constraintWriter.WriteUniqueKeys());
            //looks like a boolean check, skip it
            constraintWriter.CheckConstraintExcluder = check => (_hasBit && check.Expression.Contains(" IN (0, 1)"));
            sb.AppendLine(constraintWriter.WriteCheckConstraints());

            AddIndexes(sb);

            return sb.ToString();
        }

        private ConstraintWriter CreateConstraintWriter()
        {
            return new ConstraintWriter(Table)
            {
                IncludeSchema = IncludeSchema,
                TranslateCheckConstraint = TranslateCheckExpression,
                EscapeNames = EscapeNames
            };
        }

        private static string TranslateCheckExpression(string expression)
        {
            //translate SqlServer-isms into PostgreSql
            expression = SqlTranslator.EnsureCurrentTimestamp(expression);

            return expression
                //column escaping
                .Replace("[", "\"")
                .Replace("]", "\"")
                //MySql column escaping
                .Replace("`", "\"")
                .Replace("`", "\"");
        }

        protected virtual IMigrationGenerator CreateMigrationGenerator()
        {
            return new PostgreSqlMigrationGenerator { IncludeSchema = IncludeSchema, EscapeNames = EscapeNames };
        }

        private void AddIndexes(StringBuilder sb)
        {
            if (!Table.Indexes.Any()) return;

            var migration = CreateMigrationGenerator();
            foreach (var index in Table.Indexes)
            {
                if (index.IsUniqueKeyIndex(Table)) continue;

                sb.AppendLine(migration.AddIndex(Table, index));
            }
        }

        protected override ISqlFormatProvider SqlFormatProvider()
        {
            return new SqlFormatProvider();
        }

        protected override string WriteDataType(DatabaseColumn column)
        {
            var defaultValue = string.Empty;
            if (!string.IsNullOrEmpty(column.DefaultValue))
            {
                defaultValue = WriteDefaultValue(column);
            }

            var sql = DataTypeWriter.WriteDataType(column);
            if (sql == "BIT") _hasBit = true;

            if (column.IsAutoNumber)
            {
                var id = column.IdentityDefinition ?? new DatabaseColumnIdentity();
                bool isLong = column.DataType != null && column.DataType.GetNetType() == typeof(long);
                // Non trivial identities are hooked to a sequence up by AutoIncrementWriter.
                // Newer postgres versions require specifying UNIQUE explicitly.
                if (id.IsNonTrivialIdentity())
                    sql = (isLong ? " BIGINT" : " INT") + " NOT NULL UNIQUE";
                else
                    sql = isLong ? " BIGSERIAL" : " SERIAL";
            }
            else
            {
                if (column.IsPrimaryKey)
                    sql += " NOT NULL" + defaultValue;
                else
                    sql += " " + (!column.Nullable ? " NOT NULL" : string.Empty) + defaultValue;
            }
            return sql;
        }

        private static bool IsBooleanColumn(DatabaseColumn column)
        {
            if (column.DataType == null)
            {
                return string.Equals(column.DbDataType, "bool", StringComparison.OrdinalIgnoreCase);
            }
            return column.DataType.GetNetType() == typeof(bool);
        }

        private static string WriteDefaultValue(DatabaseColumn column)
        {
            const string defaultConstraint = " DEFAULT ";
            var defaultValue = FixDefaultValue(column.DefaultValue).Trim();
            if (defaultValue.StartsWith("(") && defaultValue.EndsWith(")"))
            {
                defaultValue = defaultValue.Substring(1, defaultValue.Length - 2);
            }
            if (IsStringColumn(column))
            {
                //if it already has quotes, trim them
                if (defaultValue.StartsWith("'") && !defaultValue.EndsWith("'"))
                {
                    return defaultConstraint + defaultValue;
                }
                return defaultConstraint + "'" + defaultValue.Trim('\'') + "'";
            }

            if (IsBooleanColumn(column))
            {
                switch (defaultValue.ToUpperInvariant())
                {
                    //true, yes, on, 1 or prefixes- t or y
                    case "T":
                    case "TRUE":
                    case "ON":
                    case "1":
                    case "Y":
                    case "YES":
                        defaultValue = "TRUE";
                        break;

                    default:
                        defaultValue = "FALSE";
                        break;
                }
                return defaultConstraint + defaultValue;
            }

            //numeric default
            string d = defaultValue;
            ////remove any parenthesis except for common functions (nextval, now, random). Any others?
            //if (defaultValue.IndexOf("nextval(", StringComparison.OrdinalIgnoreCase) != -1)
            //{
            //    d
            //}
            //else if (defaultValue.IndexOf("now(", StringComparison.OrdinalIgnoreCase) != -1)
            //{
            //    d = defaultValue;
            //}
            //else if (defaultValue.IndexOf("random(", StringComparison.OrdinalIgnoreCase) != -1)
            //{
            //    d = defaultValue;
            //}
            //else
            //{
            //    d = defaultValue.Trim(new[] { '(', ')' });
            //}
            //special case casting. What about other single integers?
            if ("money".Equals(column.DbDataType, StringComparison.OrdinalIgnoreCase) && d == "0")
                d = "((0::text)::money)"; //cast from int to money. Weird.
            defaultValue = defaultConstraint + d;
            return defaultValue;
        }

        private static bool IsStringColumn(DatabaseColumn column)
        {
            var dataType = column.DbDataType.ToUpperInvariant();
            var isString = (dataType == "VARCHAR" || dataType == "TEXT" || dataType == "CHAR");
            var dt = column.DataType;
            if (dt != null && dt.IsString) isString = true;
            return isString;
        }

        private static string FixDefaultValue(string defaultValue)
        {
            //Guid defaults.
            if (SqlTranslator.IsGuidGenerator(defaultValue))
            {
                return "uuid_generate_v1()"; //use uuid-osp contrib
            }
            return SqlTranslator.Fix(defaultValue);
        }

        protected override string NonNativeAutoIncrementWriter()
        {
            return new AutoIncrementWriter(Table).Write();
        }
    }
}