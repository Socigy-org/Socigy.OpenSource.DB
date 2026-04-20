using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Text;

namespace Socigy.OpenSource.DB.Core.Convertors
{
#nullable enable
    public class DbEnumValueConvertor<T> : IDbValueConvertor<T>
    {
        private readonly Type _UnderlayingType;
        private readonly Dictionary<string, object> _ValueMapping;
        public DbEnumValueConvertor()
        {
            var enumType = typeof(T);
            if (!enumType.IsEnum)
            {
                throw new InvalidOperationException($"Type {typeof(T).FullName} is not an Enum type.");
            }

            _UnderlayingType = Enum.GetUnderlyingType(enumType);
            _ValueMapping = [];

            var enumNames = Enum.GetNames(enumType);
            for (int i = 0; i < enumNames.Length; i++)
            {
                var name = enumNames[i];
                var enumValue = Enum.Parse(enumType, name);

                var underlyingValue = Convert.ChangeType(enumValue, _UnderlayingType);
                _ValueMapping.Add(name, underlyingValue);
            }
        }

        public T? ConvertFromDbValue(object? dbValue)
        {
            if (dbValue == null || dbValue == DBNull.Value)
            {
                return default;
            }
            else if (dbValue is string enumValueName)
            {
                return (T?)Enum.Parse(typeof(T), enumValueName);
            }
            else if (dbValue.GetType() == _UnderlayingType)
            {
                return Enum.ToObject(typeof(T), dbValue) is T enumValue ? enumValue : default;
            }
            else
                throw new InvalidOperationException($"Tried to convert DB value {dbValue.GetType().FullName} to {typeof(T).FullName} enum. Value: '{dbValue}'");
        }

        public object? ConvertToDbValue(T? value)
        {
            if (value == null)
                return null;

            return Convert.ChangeType(value, _UnderlayingType);
        }
    }
#nullable disable
}
