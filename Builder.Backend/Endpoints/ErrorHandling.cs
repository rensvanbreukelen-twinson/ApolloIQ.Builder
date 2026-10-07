using Builder.Backend.Contracts;
using Builder.Core.Model;
using Builder.Simulator;

namespace Builder.Backend.Endpoints;

public static class ErrorHandling
{
    public static IApplicationBuilder UseProjectErrors(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            try
            {
                await next(context);
            }
            catch (ProjectException ex)
            {
                var (status, field) = ex.Code switch
                {
                    ProjectErrors.NotFound => (StatusCodes.Status404NotFound, (string?)null),
                    ProjectErrors.DuplicateName => (StatusCodes.Status409Conflict, "name"),
                    ProjectErrors.InvalidName => (StatusCodes.Status400BadRequest, "name"),
                    ProjectErrors.InvalidParent or ProjectErrors.Cycle => (StatusCodes.Status400BadRequest, "parentId"),
                    _ => (StatusCodes.Status400BadRequest, null)
                };
                context.Response.StatusCode = status;
                await context.Response.WriteAsJsonAsync(new ApiError(ex.Code, ex.Message, ex.Field ?? field));
            }
            catch (SimulationException ex)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await context.Response.WriteAsJsonAsync(new ApiError("simulation", ex.Message));
            }
        });
}
