using System;
using Microsoft.AspNetCore.Mvc;

namespace TenkoServer.Controllers
{
    [ApiController]
    [Route("health")]
    [Route("api/v1/health")]
    public class HealthController : ControllerBase
    {
        [HttpGet]
        public IActionResult GetHealth()
        {
            return Ok(new
            {
                status = "healthy",
                timestamp = DateTime.UtcNow
            });
        }
    }
}
