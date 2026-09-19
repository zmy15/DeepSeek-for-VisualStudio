using System;
using System.ComponentModel;
using DeepSeek_v4_for_VisualStudio.Services;

namespace DeepSeek_v4_for_VisualStudio.Settings
{
    /// <summary>
    /// 本地化的 CategoryAttribute，通过 LocalizationService 在运行时解析分类名称。
    /// .NET Framework 中 Category 属性非 virtual 且 GetLocalizedString() 仅首次调用。
    /// 解决方案：重写 GetLocalizedString() 提供初值，语言切换时用反射
    /// 直接改写基类内部 categoryValue 字段，绕过 localized 缓存标志。
    /// </summary>
    [AttributeUsage(AttributeTargets.Property | AttributeTargets.Class, AllowMultiple = false)]
    internal class LocalizedCategoryAttribute : CategoryAttribute
    {
        private static readonly System.Reflection.FieldInfo[] CategoryValueFields =
            CollectCategoryValueFields();

        private readonly string _key;

        public LocalizedCategoryAttribute(string key) : base(key)
        {
            _key = key ?? string.Empty;
            LocalizationService.Instance.LanguageChanged += OnLanguageChanged;
        }

        protected override string GetLocalizedString(string value)
        {
            return LocalizationService.Instance[_key];
        }

        private void OnLanguageChanged(object? sender, EventArgs e)
        {
            RefreshCategoryValue();
        }

        /// <summary>
        /// 直接改写基类保存分类值的私有字段，绕过 CategoryAttribute 的首次读取缓存。
        /// 字段名在不同 .NET 运行时为 categoryValue 或 _categoryValue。
        /// </summary>
        internal void RefreshCategoryValue()
        {
            if (CategoryValueFields.Length == 0)
            {
                return;
            }

            string value = LocalizationService.Instance[_key];
            try
            {
                foreach (var field in CategoryValueFields)
                {
                    field.SetValue(this, value);
                }
            }
            catch
            {
                // 静默忽略
            }
        }

        private static System.Reflection.FieldInfo[] CollectCategoryValueFields()
        {
            const System.Reflection.BindingFlags flags =
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;

            var fields = new System.Collections.Generic.List<System.Reflection.FieldInfo>(1);
            try
            {
                foreach (var field in typeof(CategoryAttribute).GetFields(flags))
                {
                    if (field.FieldType == typeof(string)
                        && field.Name.IndexOf("categoryvalue", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        fields.Add(field);
                    }
                }
            }
            catch
            {
                // 反射失败时 CategoryAttribute 自身的 GetLocalizedString 仍可提供当前值
            }

            return fields.ToArray();
        }
    }

    /// <summary>
    /// 本地化的 DisplayNameAttribute，通过 LocalizationService 在运行时解析显示名称。
    /// 用法: [LocalizedDisplayName("settings.apiKey.displayName")]
    /// </summary>
    [AttributeUsage(AttributeTargets.Property | AttributeTargets.Event | AttributeTargets.Class, AllowMultiple = false)]
    internal class LocalizedDisplayNameAttribute : DisplayNameAttribute
    {
        private readonly string _key;

        public LocalizedDisplayNameAttribute(string key)
        {
            _key = key ?? string.Empty;
        }

        /// <summary>
        /// 重写 DisplayName 以返回本地化后的显示名称。
        /// </summary>
        public override string DisplayName => LocalizationService.Instance[_key];
    }

    /// <summary>
    /// 本地化的 DescriptionAttribute，通过 LocalizationService 在运行时解析描述文本。
    /// 用法: [LocalizedDescription("settings.apiKey.description")]
    /// </summary>
    [AttributeUsage(AttributeTargets.Property | AttributeTargets.Event | AttributeTargets.Class, AllowMultiple = false)]
    internal class LocalizedDescriptionAttribute : DescriptionAttribute
    {
        private readonly string _key;

        public LocalizedDescriptionAttribute(string key)
        {
            _key = key ?? string.Empty;
        }

        /// <summary>
        /// 重写 Description 以返回本地化后的描述文本。
        /// </summary>
        public override string Description => LocalizationService.Instance[_key];
    }

    /// <summary>
    /// 属性网格本地化刷新工具。
    /// .NET Framework 的 MemberDescriptor 在首次读取 Description 或 Category 后会把结果缓存到私有字段
    /// （description / _description、category / _category）中，语言切换时该缓存不会自动失效，
    /// TypeDescriptor.Refresh() 也不会清空它，
    /// 于是 VS 选项页会出现“属性名已切换、分组标题或描述仍旧显示旧语言”的现象。
    /// 这里在语言变更时用反射清空缓存字段，再触发刷新事件让属性网格重新读取描述。
    /// </summary>
    internal static class LocalizedPropertyGridRefresh
    {
        /// <summary>
        /// MemberDescriptor 缓存 Description 的私有字段名随运行时版本不同
        /// （.NET Framework 4.8 为 description，部分运行时为 _description），
        /// 因此扫描所有非公开字符串字段，凡是用于缓存描述或分类的都收集起来。
        /// </summary>
        private static readonly System.Reflection.FieldInfo[] DescriptionCacheFields =
            CollectStringCacheFields("description");

        private static readonly System.Reflection.FieldInfo[] CategoryCacheFields =
            CollectStringCacheFields("category");

        private static System.Reflection.FieldInfo[] CollectStringCacheFields(string cacheName)
        {
            const System.Reflection.BindingFlags flags =
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;

            var fields = new System.Collections.Generic.List<System.Reflection.FieldInfo>(1);
            try
            {
                foreach (var field in typeof(MemberDescriptor).GetFields(flags))
                {
                    if (field.FieldType == typeof(string)
                        && field.Name.IndexOf(cacheName, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        fields.Add(field);
                    }
                }
            }
            catch
            {
                // 反射失败时返回空数组，Refresh 仍会发出属性网格刷新事件
            }

            return fields.ToArray();
        }

        /// <summary>
        /// 刷新分类属性并清空指定选项页类型的标题、描述缓存，再通知属性网格重新读取本地化文本。
        /// </summary>
        internal static void Refresh(Type pageType)
        {
            if (pageType is null)
            {
                return;
            }

            if (CategoryCacheFields.Length > 0 || DescriptionCacheFields.Length > 0)
            {
                try
                {
                    foreach (PropertyDescriptor descriptor in TypeDescriptor.GetProperties(pageType))
                    {
                        // CategoryAttribute 实例可能先于本方法收到 LanguageChanged 事件，这里主动刷新，
                        // 确保随后属性网格重建分组时读到的是当前语言。
                        foreach (Attribute attribute in descriptor.Attributes)
                        {
                            if (attribute is LocalizedCategoryAttribute localizedCategory)
                            {
                                localizedCategory.RefreshCategoryValue();
                            }
                        }

                        // 置空后，下次读取 Category / Description 会重新经过本地化属性。
                        foreach (var field in CategoryCacheFields)
                        {
                            field.SetValue(descriptor, null);
                        }

                        // 置空后，下次读取 Description 会重新经过 LocalizedDescriptionAttribute
                        foreach (var field in DescriptionCacheFields)
                        {
                            field.SetValue(descriptor, null);
                        }
                    }
                }
                catch
                {
                    // 反射失败时静默降级，至少保证下方 Refresh 事件仍然发出
                }
            }

            TypeDescriptor.Refresh(pageType);
        }
    }
}
