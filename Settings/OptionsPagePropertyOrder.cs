using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;

namespace DeepSeek_v4_for_VisualStudio.Settings
{
    /// <summary>
    /// 为 DeepSeek 选项页提供稳定的属性显示顺序。
    /// </summary>
    internal sealed class OptionsPageTypeDescriptionProvider : TypeDescriptionProvider
    {
        private readonly PropertyDescriptorCollection _properties;

        public OptionsPageTypeDescriptionProvider(PropertyDescriptorCollection properties)
        {
            _properties = properties;
        }

        public override ICustomTypeDescriptor GetTypeDescriptor(Type objectType, object? instance)
            => new OrderedTypeDescriptor(_properties, instance);
    }

    internal sealed class OrderedTypeDescriptor : CustomTypeDescriptor
    {
        private static readonly IReadOnlyDictionary<string, int> PropertyOrder =
            new[]
            {
                nameof(DeepSeekOptionsPage.ApiKey),
                nameof(DeepSeekOptionsPage.ApiBaseUrl),
                nameof(DeepSeekOptionsPage.CustomApiKey),
                nameof(DeepSeekOptionsPage.TestConnection),
                nameof(DeepSeekOptionsPage.CustomModelPicker),
                nameof(DeepSeekOptionsPage.CustomModelName),
                nameof(DeepSeekOptionsPage.ModelMaxTokenLimits),
                nameof(DeepSeekOptionsPage.CustomVisionModels),
                nameof(DeepSeekOptionsPage.SelectedModelChoice),
                nameof(DeepSeekOptionsPage.IsThinkingEnabled),
                nameof(DeepSeekOptionsPage.ReasoningEffort),
            }
            .Select((name, index) => new { name, index })
            .ToDictionary(item => item.name, item => item.index, StringComparer.Ordinal);

        private readonly PropertyDescriptorCollection _properties;
        private readonly object? _instance;

        public OrderedTypeDescriptor(PropertyDescriptorCollection properties, object? instance)
        {
            _properties = properties;
            _instance = instance;
        }

        public override PropertyDescriptorCollection GetProperties()
            => GetProperties(null);

        public override PropertyDescriptorCollection GetProperties(Attribute[]? attributes)
        {
            IEnumerable<PropertyDescriptor> filtered = _properties.Cast<PropertyDescriptor>();
            if (attributes != null && attributes.Length > 0)
                filtered = filtered.Where(property => MatchesAttributes(property, attributes));

            var ordered = filtered
                .Select((property, originalIndex) => new { property, originalIndex })
                .OrderBy(item => GetOrder(item.property.Name))
                .ThenBy(item => item.originalIndex)
                .Select(item => item.property)
                .ToArray();

            return new PropertyDescriptorCollection(ordered, readOnly: true);
        }

        public override object? GetPropertyOwner(PropertyDescriptor? pd) => _instance;

        private static bool MatchesAttributes(PropertyDescriptor property, Attribute[] attributes)
        {
            foreach (Attribute required in attributes)
            {
                Attribute? actual = property.Attributes.Cast<Attribute>()
                    .FirstOrDefault(candidate => candidate.TypeId.Equals(required.TypeId));
                if (actual == null || !actual.Equals(required))
                    return false;
            }

            return true;
        }

        private static int GetOrder(string propertyName)
            => PropertyOrder.TryGetValue(propertyName, out int order)
                ? order
                : int.MaxValue;
    }
}