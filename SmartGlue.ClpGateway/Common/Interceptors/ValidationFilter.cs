using FluentValidation;

namespace SmartGlue.ClpGateway.Common.Interceptors;

public class ValidationFilter<T> : IEndpointFilter where T : class
{
  public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
  {
    if (context.Arguments.FirstOrDefault(x => x is T) is not T validatable)
      return await next(context);

    var validationResult = await context.HttpContext.RequestServices.GetRequiredService<IValidator<T>>().ValidateAsync(validatable);

    return validationResult.IsValid ? await next(context) : Results.ValidationProblem(validationResult.ToDictionary());
  }
}
