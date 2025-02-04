using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Xunit.Internal;
using Xunit.Sdk;

namespace Xunit;

/// <summary>
/// Provides a base class for attributes that will provide member data. The member data must return
/// something compatible with <see cref="IEnumerable"/>.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true, Inherited = true)]
public abstract class MemberDataAttributeBase : DataAttribute
{
	/// <summary>
	/// Initializes a new instance of the <see cref="MemberDataAttributeBase"/> class.
	/// </summary>
	/// <param name="memberName">The name of the public static member on the test class that will provide the test data</param>
	/// <param name="parameters">The parameters for the member (only supported for methods; ignored for everything else)</param>
	protected MemberDataAttributeBase(
		string memberName,
		object?[] parameters)
	{
		MemberName = Guard.ArgumentNotNull(memberName);
		Parameters = Guard.ArgumentNotNull(parameters);
	}

	/// <summary>
	/// Returns <c>true</c> if the data attribute wants to skip enumerating data during discovery.
	/// This will cause the theory to yield a single test case for all data, and the data discovery
	/// will be during test execution instead of discovery.
	/// </summary>
	public bool DisableDiscoveryEnumeration { get; set; }

	/// <summary>
	/// Gets the member name.
	/// </summary>
	public string MemberName { get; }

	/// <summary>
	/// Gets or sets the type to retrieve the member from. If not set, then the property will be
	/// retrieved from the unit test class.
	/// </summary>
	public Type? MemberType { get; set; }

	/// <summary>
	/// Gets or sets the parameters passed to the member. Only supported for static methods.
	/// </summary>
	public object?[] Parameters { get; }

	/// <inheritdoc/>
	protected override ITheoryDataRow ConvertDataRow(
		MethodInfo testMethod,
		object dataRow)
	{
		Guard.ArgumentNotNull(testMethod);
		Guard.ArgumentNotNull(dataRow);

		try
		{
			return base.ConvertDataRow(testMethod, dataRow);
		}
		catch (ArgumentException)
		{
			throw new ArgumentException($"Member '{MemberName}' on '{MemberType ?? testMethod.DeclaringType}' yielded an item of type '{dataRow?.GetType().SafeName()}' which is not an 'object?[]', 'Xunit.ITheoryDataRow' or 'System.Runtime.CompilerServices.ITuple'", nameof(dataRow));
		}
	}

	/// <inheritdoc/>
	public override ValueTask<IReadOnlyCollection<ITheoryDataRow>?> GetData(MethodInfo testMethod)
	{
		Guard.ArgumentNotNull(testMethod);

		var type = MemberType ?? testMethod.DeclaringType;
		if (type is null)
			return new(default(IReadOnlyCollection<ITheoryDataRow>));

		var accessor = GetPropertyAccessor(type) ?? GetFieldAccessor(type) ?? GetMethodAccessor(type);
		if (accessor is null)
		{
			var parameterText = Parameters?.Length > 0 ? $" with parameter types: {string.Join(", ", Parameters.Select(p => p?.GetType().FullName ?? "(null)"))}" : "";
			throw new ArgumentException($"Could not find public static member (property, field, or method) named '{MemberName}' on {type.FullName}{parameterText}");
		}

		var returnValue = accessor();
		if (returnValue is null)
			return new(default(IReadOnlyCollection<ITheoryDataRow>));

		if (returnValue is IEnumerable dataItems)
		{
			var result = new List<ITheoryDataRow>();

			foreach (var dataItem in dataItems)
				if (dataItem is not null)
					result.Add(ConvertDataRow(testMethod, dataItem));

			return new(result.CastOrToReadOnlyCollection());
		}

		return GetDataAsync(returnValue, testMethod, type);
	}

	async ValueTask<IReadOnlyCollection<ITheoryDataRow>?> GetDataAsync(
		object? returnValue,
		MethodInfo testMethod,
		Type type)
	{
		var taskAwaitable = returnValue.AsValueTask();
		if (taskAwaitable.HasValue)
			returnValue = await taskAwaitable.Value;

		if (returnValue is IAsyncEnumerable<object?> asyncDataItems)
		{
			var result = new List<ITheoryDataRow>();

			await foreach (var dataItem in asyncDataItems)
				if (dataItem is not null)
					result.Add(ConvertDataRow(testMethod, dataItem));

			return result.CastOrToReadOnlyCollection();
		}

		// Duplicate from GetData(), but it's hard to avoid since we need to support Task/ValueTask
		// of IEnumerable (and not just IAsyncEnumerable).
		if (returnValue is IEnumerable dataItems)
		{
			var result = new List<ITheoryDataRow>();

			foreach (var dataItem in dataItems)
				if (dataItem is not null)
					result.Add(ConvertDataRow(testMethod, dataItem));

			return result.CastOrToReadOnlyCollection();
		}

		throw new ArgumentException(
			$"Member '{MemberName}' on '{type.FullName}' must return data in one of the following formats:" + Environment.NewLine +
			"- IEnumerable<ITheoryDataRow>" + Environment.NewLine +
			"- Task<IEnumerable<ITheoryDataRow>>" + Environment.NewLine +
			"- ValueTask<IEnumerable<ITheoryDataRow>>" + Environment.NewLine +
			"- IEnumerable<object[]>" + Environment.NewLine +
			"- Task<IEnumerable<object[]>>" + Environment.NewLine +
			"- ValueTask<IEnumerable<object[]>>" + Environment.NewLine +
			"- IAsyncEnumerable<ITheoryDataRow>" + Environment.NewLine +
			"- Task<IAsyncEnumerable<ITheoryDataRow>>" + Environment.NewLine +
			"- ValueTask<IAsyncEnumerable<ITheoryDataRow>>" + Environment.NewLine +
			"- IAsyncEnumerable<object[]>" + Environment.NewLine +
			"- Task<IAsyncEnumerable<object[]>>" + Environment.NewLine +
			"- ValueTask<IAsyncEnumerable<object[]>>"
		);
	}

	Func<object?>? GetFieldAccessor(Type? type)
	{
		FieldInfo? fieldInfo = null;
		for (var reflectionType = type; reflectionType is not null; reflectionType = reflectionType.BaseType)
		{
			fieldInfo = reflectionType.GetRuntimeField(MemberName);
			if (fieldInfo is not null)
				break;
		}

		if (fieldInfo is null || !fieldInfo.IsStatic)
			return null;

		return () => fieldInfo.GetValue(null);
	}

	Func<object?>? GetMethodAccessor(Type? type)
	{
		MethodInfo? methodInfo = null;
		var parameterTypes = Parameters is null ? Array.Empty<Type>() : Parameters.Select(p => p?.GetType()).ToArray();
		for (var reflectionType = type; reflectionType is not null; reflectionType = reflectionType.BaseType)
		{
			methodInfo =
				reflectionType
					.GetRuntimeMethods()
					.FirstOrDefault(m => m.Name == MemberName && ParameterTypesCompatible(m.GetParameters(), parameterTypes));

			if (methodInfo is not null)
				break;
		}

		if (methodInfo is null || !methodInfo.IsStatic)
			return null;

		return () => methodInfo.Invoke(null, Parameters);
	}

	Func<object?>? GetPropertyAccessor(Type? type)
	{
		PropertyInfo? propInfo = null;
		for (var reflectionType = type; reflectionType is not null; reflectionType = reflectionType.BaseType)
		{
			propInfo = reflectionType.GetRuntimeProperty(MemberName);
			if (propInfo is not null)
				break;
		}

		if (propInfo is null || propInfo.GetMethod is null || !propInfo.GetMethod.IsStatic)
			return null;

		return () => propInfo.GetValue(null, null);
	}

	static bool ParameterTypesCompatible(
		ParameterInfo[]? parameters,
		Type?[] parameterTypes)
	{
		if (parameters?.Length != parameterTypes.Length)
			return false;

		for (var idx = 0; idx < parameters.Length; ++idx)
			if (parameterTypes[idx] is not null && !parameters[idx].ParameterType.IsAssignableFrom(parameterTypes[idx]!))
				return false;

		return true;
	}
}
