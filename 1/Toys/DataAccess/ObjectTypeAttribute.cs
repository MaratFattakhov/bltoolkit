using System;

namespace Rsdn.Framework.DataAccess
{
	[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
	public class ObjectTypeAttribute : Attribute
	{
		public ObjectTypeAttribute(Type objectType)
		{
			_objectType = objectType;
		}

		private Type _objectType;
		public  Type  ObjectType
		{
			get { return _objectType; }
		}
	}
}