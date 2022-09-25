namespace Injectio.Attributes;

/// <summary>Attribute to indicate the method should be called to register services</summary>
/// <example>use the RegisterModule attribute
///   <code>
///   public class RegistrationModule
///   {
///       [RegisterModule]
///       public static void Register(IServiceCollection services)
///       {
///           services.TryAddTransient&lt;IModuleService, ModuleService&gt;();
///       }
///   }
///   </code>
/// </example>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public class RegisterModuleAttribute : Attribute
{

}
