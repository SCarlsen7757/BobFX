using BobFx.Core.Services;
using Microsoft.AspNetCore.Mvc;

namespace BobFx.Core.Controllers;

[ApiController]
[Route("api/ui")]
public sealed class UiController(UiStateService state) : ControllerBase
{
    [HttpGet("state")]
    public UiState State() => state.GetState();
}
