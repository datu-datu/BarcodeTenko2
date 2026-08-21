using System.Collections.Generic;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using TenkoServer.Models;
using TenkoServer.Models.DTOs;

namespace TenkoServer.Controllers
{
    [ApiController]
    [Route("api/v1/auth")]
    public class AuthController : ControllerBase
    {
        private readonly TenkoServerOptions _options;

        public AuthController(IOptions<TenkoServerOptions> options)
        {
            _options = options.Value;
        }

        [HttpPost("login")]
        [EnableRateLimiting("auth")]
        public async Task<ActionResult<LoginResponseDto>> Login([FromBody] LoginRequestDto request)
        {
            if (string.IsNullOrEmpty(request.Password))
            {
                return BadRequest(new LoginResponseDto { Success = false, Message = "パスワードを入力してください。" });
            }

            if (request.Password != _options.AdminPassword)
            {
                return Unauthorized(new LoginResponseDto { Success = false, Message = "パスワードが正しくありません。" });
            }

            var claims = new List<Claim>
            {
                new Claim(ClaimTypes.Name, "Admin"),
                new Claim(ClaimTypes.Role, "Administrator")
            };

            var claimsIdentity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
            var authProperties = new AuthenticationProperties
            {
                IsPersistent = true,
                ExpiresUtc = DateTimeOffset.UtcNow.AddDays(7)
            };

            await HttpContext.SignInAsync(
                CookieAuthenticationDefaults.AuthenticationScheme,
                new ClaimsPrincipal(claimsIdentity),
                authProperties);

            return Ok(new LoginResponseDto { Success = true, Message = "ログインに成功しました。" });
        }

        [HttpPost("logout")]
        public async Task<ActionResult<LoginResponseDto>> Logout()
        {
            await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Ok(new LoginResponseDto { Success = true, Message = "ログアウトしました。" });
        }

        [HttpGet("status")]
        public ActionResult GetStatus()
        {
            return Ok(new
            {
                isAuthenticated = User.Identity?.IsAuthenticated ?? false,
                user = User.Identity?.IsAuthenticated == true ? "Admin" : null
            });
        }
    }
}
