using System.Net;
using System.Net.Mail;
using System.Text.RegularExpressions;
using Asp.Versioning;
using Agent360.API.Abstractions;
using Agent360.API.Constants;
using Npgsql;

namespace Agent360.API.Features.Contact;

public sealed record ContactFormRequest(
    string Name,
    string Email,
    string Subject,
    string Message,
    string? Category = "general",
    string? RequestedAgentRole = null,
    string? Company = null,
    string? Phone = null,
    string? Language = "vi"
);

public sealed class ContactEndpoints : IEndpoint
{
    private static readonly Regex EmailRegex = new(
        @"^[^@\s]+@[^@\s]+\.[^@\s]+$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public void MapEndpoint(IEndpointRouteBuilder routeBuilder)
    {
        var group = routeBuilder.MapGroup("contact")
            .MapToApiVersion(ApiVersion.Default)
            .WithTags("Contact");

        async Task EnsureTablesAsync(NpgsqlConnection conn, CancellationToken ct)
        {
            var ddlStatements = new[]
            {
                @"CREATE SCHEMA IF NOT EXISTS user_management;",

                @"CREATE TABLE IF NOT EXISTS user_management.""ContactSubmissions"" (
                    ""Id"" bigserial PRIMARY KEY,
                    ""Name"" text NOT NULL,
                    ""Email"" text NOT NULL,
                    ""Subject"" text NOT NULL,
                    ""Message"" text NOT NULL,
                    ""Category"" text NOT NULL DEFAULT 'general',
                    ""RequestedAgentRole"" text,
                    ""Company"" text,
                    ""Phone"" text,
                    ""Status"" text NOT NULL DEFAULT 'new',
                    ""Language"" text NOT NULL DEFAULT 'vi',
                    ""CreatedAt"" timestamptz NOT NULL DEFAULT NOW()
                );"
            };

            foreach (var ddl in ddlStatements)
            {
                await using var cmd = new NpgsqlCommand(ddl, conn);
                await cmd.ExecuteNonQueryAsync(ct);
            }
        }

        group.MapPost("", async (
            ContactFormRequest request,
            HttpContext httpContext,
            IConfiguration config,
            ILogger<ContactEndpoints> logger,
            CancellationToken ct) =>
        {
            // 1. Validation & Anti-Spam checks
            if (string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrWhiteSpace(request.Message))
            {
                return Results.BadRequest(new { message = "Name, email, and message are required." });
            }

            var trimmedEmail = request.Email.Trim();
            if (!EmailRegex.IsMatch(trimmedEmail) || trimmedEmail.Length > 254)
            {
                return Results.BadRequest(new { message = "Invalid email format." });
            }

            if (request.Name.Length > 150)
            {
                return Results.BadRequest(new { message = "Name cannot exceed 150 characters." });
            }

            if (request.Message.Length > 4000)
            {
                return Results.BadRequest(new { message = "Message cannot exceed 4000 characters." });
            }

            // Determine language preference (Explicit param or header)
            var acceptLang = httpContext.Request.Headers.AcceptLanguage.ToString().ToLowerInvariant();
            var isEnglish = string.Equals(request.Language, "en", StringComparison.OrdinalIgnoreCase) ||
                            (!string.Equals(request.Language, "vi", StringComparison.OrdinalIgnoreCase) && acceptLang.Contains("en") && !acceptLang.Contains("vi"));

            var isAgentRequest = !string.IsNullOrWhiteSpace(request.RequestedAgentRole) || 
                                 string.Equals(request.Category, "request_agent", StringComparison.OrdinalIgnoreCase);

            var connStr = config.GetConnectionString("DefaultConnection") ?? config.GetConnectionString("Postgres");
            if (!string.IsNullOrWhiteSpace(connStr))
            {
                try
                {
                    await using var conn = new NpgsqlConnection(connStr);
                    await conn.OpenAsync(ct);
                    await EnsureTablesAsync(conn, ct);

                    await using var cmd = new NpgsqlCommand(@"
                        INSERT INTO user_management.""ContactSubmissions""
                        (""Name"", ""Email"", ""Subject"", ""Message"", ""Category"", ""RequestedAgentRole"", ""Company"", ""Phone"", ""Language"", ""CreatedAt"")
                        VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, NOW())
                        RETURNING ""Id"";
                    ", conn);
                    cmd.Parameters.AddWithValue(request.Name.Trim());
                    cmd.Parameters.AddWithValue(trimmedEmail);
                    cmd.Parameters.AddWithValue(request.Subject ?? (isAgentRequest ? "New Agent Request" : "Inquiry"));
                    cmd.Parameters.AddWithValue(request.Message.Trim());
                    cmd.Parameters.AddWithValue(request.Category ?? "general");
                    cmd.Parameters.AddWithValue((object?)request.RequestedAgentRole ?? DBNull.Value);
                    cmd.Parameters.AddWithValue((object?)request.Company ?? DBNull.Value);
                    cmd.Parameters.AddWithValue((object?)request.Phone ?? DBNull.Value);
                    cmd.Parameters.AddWithValue(isEnglish ? "en" : "vi");
                    await cmd.ExecuteScalarAsync(ct);
                }
                catch (Exception dbEx)
                {
                    logger.LogWarning(dbEx, "[Contact] Failed to persist submission to database, proceeding with email delivery");
                }
            }

            // Deliver via SMTP
            var smtpHost = config["SMTP_HOST"] ?? "mail.hmz.one";
            var smtpPort = int.TryParse(config["SMTP_PORT"], out var p) ? p : 465;
            var smtpUser = config["SMTP_USER"] ?? "noreply@skyer.vn";
            var smtpPass = config["SMTP_PASSWORD"] ?? "SkyerMail2026!#NoReply";
            var replyToEmail = config["CONTACT_REPLY_TO"] ?? "info@skyer.vn";

            // 1. Email to User (Automated Confirmation - VI or EN)
            try
            {
                using var userMail = new MailMessage();
                userMail.From = new MailAddress("noreply@skyer.vn", "Skyer AI Platform");
                userMail.To.Add(new MailAddress(trimmedEmail, request.Name));
                userMail.ReplyToList.Add(new MailAddress(replyToEmail, "Skyer Support"));
                userMail.IsBodyHtml = true;

                if (isAgentRequest)
                {
                    userMail.Subject = isEnglish
                        ? $"[Skyer] Confirmation: Bespoke AI Agent Request - {request.RequestedAgentRole ?? request.Subject}"
                        : $"[Skyer] Xác nhận yêu cầu Nhân viên AI Mới - {request.RequestedAgentRole ?? request.Subject}";

                    userMail.Body = GetRequestNewAgentTemplate(
                        request.Name, 
                        request.RequestedAgentRole ?? (isEnglish ? "Custom AI Teammate" : "Chuyên viên AI theo yêu cầu"), 
                        request.Message, 
                        isEnglish);
                }
                else
                {
                    userMail.Subject = isEnglish
                        ? $"[Skyer] Thank you for contacting us - {request.Subject}"
                        : $"[Skyer] Cảm ơn bạn đã liên hệ - {request.Subject}";

                    userMail.Body = GetThanksForContactTemplate(request.Name, request.Subject ?? (isEnglish ? "General Inquiry" : "Liên hệ tư vấn"), request.Message, isEnglish);
                }

                using var client = new SmtpClient(smtpHost, smtpPort)
                {
                    EnableSsl = true,
                    Credentials = new NetworkCredential(smtpUser, smtpPass),
                    Timeout = 15000
                };
                await client.SendMailAsync(userMail, ct);
                logger.LogInformation("[Contact] Confirmation email sent to {Email} (AgentRequest: {IsAgent}, Lang: {Lang})", trimmedEmail, isAgentRequest, isEnglish ? "en" : "vi");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "[Contact] Error sending confirmation email to {Email}", trimmedEmail);
            }

            // 2. Email Notification to Skyer Team (info@skyer.vn)
            try
            {
                using var adminMail = new MailMessage();
                adminMail.From = new MailAddress("noreply@skyer.vn", "Skyer Form Submissions");
                adminMail.To.Add(new MailAddress("info@skyer.vn", "Skyer Operations"));
                adminMail.ReplyToList.Add(new MailAddress(trimmedEmail, request.Name));
                adminMail.IsBodyHtml = true;
                adminMail.Subject = $"[Skyer Contact Form] {(isAgentRequest ? "[New Agent Request]" : "[General Inquiry]")} from {request.Name}";
                adminMail.Body = $@"
<div style=""font-family: Arial, sans-serif; padding: 20px; color: #333;"">
    <h2 style=""color: #0284c7;"">New Submission Received</h2>
    <p><strong>Name:</strong> {WebUtility.HtmlEncode(request.Name)}</p>
    <p><strong>Email:</strong> {WebUtility.HtmlEncode(trimmedEmail)}</p>
    <p><strong>Phone:</strong> {WebUtility.HtmlEncode(request.Phone ?? "N/A")}</p>
    <p><strong>Company:</strong> {WebUtility.HtmlEncode(request.Company ?? "N/A")}</p>
    <p><strong>Category:</strong> {WebUtility.HtmlEncode(request.Category ?? "general")}</p>
    <p><strong>Language:</strong> {WebUtility.HtmlEncode(isEnglish ? "en" : "vi")}</p>
    {(isAgentRequest ? $"<p><strong>Requested Agent Role:</strong> {WebUtility.HtmlEncode(request.RequestedAgentRole ?? "N/A")}</p>" : "")}
    <p><strong>Subject:</strong> {WebUtility.HtmlEncode(request.Subject)}</p>
    <div style=""margin-top: 15px; padding: 15px; background: #f8fafc; border-left: 4px solid #0284c7;"">
        <strong>Message / Scope Requirement:</strong><br/>
        {WebUtility.HtmlEncode(request.Message).Replace("\n", "<br/>")}
    </div>
</div>";

                using var client = new SmtpClient(smtpHost, smtpPort)
                {
                    EnableSsl = true,
                    Credentials = new NetworkCredential(smtpUser, smtpPass),
                    Timeout = 15000
                };
                await client.SendMailAsync(adminMail, ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "[Contact] Notification email to info@skyer.vn failed");
            }

            return Results.Ok(new
            {
                success = true,
                message = isEnglish
                    ? (isAgentRequest ? "Your custom AI Agent request has been received. Our team will contact you within 24 hours." : "Thank you for reaching out! We have received your inquiry and will respond shortly.")
                    : (isAgentRequest ? "Yêu cầu xây dựng nhân viên AI đã được ghi nhận. Đội ngũ giải pháp sẽ phản hồi trong vòng 24 giờ." : "Cảm ơn bạn đã liên hệ! Chúng tôi đã nhận được thông tin và sẽ phản hồi sớm nhất.")
            });
        })
        .WithName("SubmitContactForm")
        .WithSummary("Submit a contact inquiry or request a custom AI agent specification")
        .AllowAnonymous();
    }

    private static string GetThanksForContactTemplate(string name, string subject, string message, bool isEnglish)
    {
        var title = isEnglish ? "Thank you for reaching out to Skyer" : "Cảm ơn bạn đã liên hệ với Skyer";
        var badge = isEnglish ? "Received" : "Đã tiếp nhận";
        var greeting = isEnglish ? $"Hello {WebUtility.HtmlEncode(name)}," : $"Xin chào {WebUtility.HtmlEncode(name)},";
        var intro = isEnglish
            ? $"Thank you for contacting <strong>Skyer</strong>. Our solutions team has received your message regarding <em>\"{WebUtility.HtmlEncode(subject)}\"</em>."
            : $"Cảm ơn bạn đã gửi thông tin liên hệ tới <strong>Skyer</strong>. Hệ thống và đội ngũ giải pháp đã tiếp nhận nội dung của bạn liên quan đến <em>\"{WebUtility.HtmlEncode(subject)}\"</em>.";
        var sectionTitle = isEnglish ? "Submitted Message:" : "Nội dung đã gửi:";
        var slaText = isEnglish
            ? "An AI Workforce Solutions Architect will review your operational requirements and respond with a customized proposal within <strong>24 business hours</strong>."
            : "Chuyên viên tư vấn triển khai AI Workforce sẽ phân tích nghiệp vụ và phản hồi giải pháp tối ưu tới bạn trong vòng <strong>24 giờ làm việc</strong>.";
        var ctaText = isEnglish ? "Explore Skyer Capabilities" : "Khám phá Năng lực Skyer";
        var footerNote = isEnglish
            ? "This is an automated confirmation from Skyer Platform. For urgent inquiries, please contact <a href=\"mailto:info@skyer.vn\" style=\"color: #38bdf8; text-decoration: none;\">info@skyer.vn</a>."
            : "Email này được gửi tự động từ hệ thống Skyer VN. Nếu cần hỗ trợ khẩn cấp, vui lòng gửi phản hồi về <a href=\"mailto:info@skyer.vn\" style=\"color: #38bdf8; text-decoration: none;\">info@skyer.vn</a>.";

        return $@"
<!DOCTYPE html>
<html>
<head>
  <meta charset=""utf-8"">
  <title>{title}</title>
</head>
<body style=""margin:0; padding:0; background-color:#0b0f19; font-family:'Segoe UI', Arial, sans-serif; color:#f1f5f9;"">
  <table width=""100%"" border=""0"" cellspacing=""0"" cellpadding=""0"" style=""background-color:#0b0f19; padding: 40px 15px;"">
    <tr>
      <td align=""center"">
        <table width=""100%"" max-width=""600"" border=""0"" cellspacing=""0"" cellpadding=""0"" style=""max-width:600px; background-color:#131b2e; border: 1px solid #1e293b; border-radius: 16px; overflow: hidden; box-shadow: 0 10px 30px rgba(0,0,0,0.5);"">
          <tr>
            <td style=""padding: 35px 40px; background: linear-gradient(135deg, #0e1726 0%, #172554 100%); border-bottom: 1px solid #1e293b;"">
              <table width=""100%"" border=""0"" cellspacing=""0"" cellpadding=""0"">
                <tr>
                  <td>
                    <span style=""font-size: 24px; font-weight: 800; color: #ffffff; letter-spacing: -0.5px;"">SKYER<span style=""color: #38bdf8;"">.</span></span>
                    <div style=""font-size: 13px; color: #94a3b8; margin-top: 4px;"">Autonomous AI Workforce Platform</div>
                  </td>
                  <td align=""right"">
                    <span style=""display: inline-block; padding: 6px 14px; background: rgba(56, 189, 248, 0.1); border: 1px solid rgba(56, 189, 248, 0.3); border-radius: 20px; font-size: 12px; color: #38bdf8; font-weight: 600;"">{badge}</span>
                  </td>
                </tr>
              </table>
            </td>
          </tr>
          <tr>
            <td style=""padding: 40px;"">
              <h2 style=""font-size: 20px; color: #ffffff; margin-top: 0; margin-bottom: 16px; font-weight: 700;"">{greeting}</h2>
              <p style=""font-size: 15px; line-height: 1.6; color: #cbd5e1; margin-bottom: 24px;"">
                {intro}
              </p>
              <div style=""background: #0b0f19; border: 1px solid #1e293b; border-radius: 12px; padding: 20px; margin-bottom: 28px;"">
                <div style=""font-size: 12px; font-weight: 700; color: #94a3b8; text-transform: uppercase; letter-spacing: 0.5px; margin-bottom: 10px;"">{sectionTitle}</div>
                <div style=""font-size: 14px; line-height: 1.6; color: #e2e8f0; font-style: italic;"">
                  {WebUtility.HtmlEncode(message).Replace("\n", "<br/>")}
                </div>
              </div>
              <p style=""font-size: 14px; line-height: 1.6; color: #94a3b8; margin-bottom: 30px;"">
                {slaText}
              </p>
              <table width=""100%"" border=""0"" cellspacing=""0"" cellpadding=""0"">
                <tr>
                  <td align=""center"">
                    <a href=""https://skyer.vn"" style=""display: inline-block; background: linear-gradient(135deg, #0284c7 0%, #2563eb 100%); color: #ffffff; text-decoration: none; font-size: 14px; font-weight: 600; padding: 14px 32px; border-radius: 8px; box-shadow: 0 4px 15px rgba(2, 132, 199, 0.35);"">{ctaText}</a>
                  </td>
                </tr>
              </table>
            </td>
          </tr>
          <tr>
            <td style=""padding: 24px 40px; background-color: #0b0f19; border-top: 1px solid #1e293b; text-align: center;"">
              <p style=""font-size: 12px; color: #64748b; margin: 0 0 8px 0;"">
                {footerNote}
              </p>
              <p style=""font-size: 11px; color: #475569; margin: 0;"">
                © {DateTime.UtcNow.Year} Skyer VN. All rights reserved. On-premise & Hybrid AI Workforce.
              </p>
            </td>
          </tr>
        </table>
      </td>
    </tr>
  </table>
</body>
</html>";
    }

    private static string GetRequestNewAgentTemplate(string name, string agentRole, string requirements, bool isEnglish)
    {
        var title = isEnglish ? "Confirmation: Custom AI Teammate Request - Skyer" : "Xác nhận yêu cầu cấu hình Nhân viên AI - Skyer";
        var badge = isEnglish ? "Agent Profile" : "Hồ sơ Nhân viên AI";
        var greeting = isEnglish ? $"Dear {WebUtility.HtmlEncode(name)}," : $"Kính gửi {WebUtility.HtmlEncode(name)},";
        var intro = isEnglish
            ? $"Skyer has logged your specification for a bespoke enterprise AI agent role: <strong style=\"color: #a855f7;\">{WebUtility.HtmlEncode(agentRole)}</strong>."
            : $"Skyer đã ghi nhận yêu cầu khởi tạo và đào tạo đặc thù cho vị trí: <strong style=\"color: #a855f7;\">{WebUtility.HtmlEncode(agentRole)}</strong>.";
        var labelRole = isEnglish ? "Role / Position:" : "Vị trí / Vai trò:";
        var labelInfra = isEnglish ? "Deployment Infrastructure:" : "Hạ tầng triển khai:";
        var valInfra = isEnglish ? "On-premise / Private Cloud / Hybrid (Air-gapped ready)" : "On-premise / Private Cloud / Hybrid";
        var labelTools = isEnglish ? "Integrations & MCP Tools:" : "Công cụ & MCP tích hợp:";
        var valTools = isEnglish ? "ERPCore, BaseCore, Knowledge Base, Sandboxed Chromium" : "ERPCore, BaseCore, Knowledge Base, Máy tính độc lập";
        var labelReq = isEnglish ? "Operational Workflow & Scope:" : "Mô tả tác vụ & Quy trình yêu cầu:";
        var nextSteps = isEnglish
            ? "<strong>Next Steps:</strong> Our Solution Engineering team will synthesize the Skill Graph, verify required MCP connections, and prepare baseline agent artifacts (SOUL.md & AGENTS.md) for your review."
            : "<strong>Các bước tiếp theo:</strong> Đội ngũ Solution Architect sẽ tiến hành xây dựng ma trận kỹ năng (Skill Graph), kiểm tra tích hợp MCP và gửi tài liệu kiến trúc mẫu (SOUL.md & AGENTS.md) cho vị trí này tới email của bạn.";
        var ctaText = isEnglish ? "Browse Existing AI Workforce Directory" : "Xem Danh mục Nhân viên AI có sẵn";
        var footerNote = isEnglish
            ? "For inquiries regarding security, air-gapping, or custom LLM fine-tuning, reply to this email or contact <a href=\"mailto:info@skyer.vn\" style=\"color: #c084fc; text-decoration: none;\">info@skyer.vn</a>."
            : "Mọi thắc mắc kỹ thuật hoặc bảo mật vui lòng phản hồi trực tiếp email này hoặc liên hệ <a href=\"mailto:info@skyer.vn\" style=\"color: #c084fc; text-decoration: none;\">info@skyer.vn</a>.";

        return $@"
<!DOCTYPE html>
<html>
<head>
  <meta charset=""utf-8"">
  <title>{title}</title>
</head>
<body style=""margin:0; padding:0; background-color:#0b0f19; font-family:'Segoe UI', Arial, sans-serif; color:#f1f5f9;"">
  <table width=""100%"" border=""0"" cellspacing=""0"" cellpadding=""0"" style=""background-color:#0b0f19; padding: 40px 15px;"">
    <tr>
      <td align=""center"">
        <table width=""100%"" max-width=""600"" border=""0"" cellspacing=""0"" cellpadding=""0"" style=""max-width:600px; background-color:#131b2e; border: 1px solid #1e293b; border-radius: 16px; overflow: hidden; box-shadow: 0 10px 30px rgba(0,0,0,0.5);"">
          <tr>
            <td style=""padding: 35px 40px; background: linear-gradient(135deg, #0e1726 0%, #1e1b4b 100%); border-bottom: 1px solid #1e293b;"">
              <table width=""100%"" border=""0"" cellspacing=""0"" cellpadding=""0"">
                <tr>
                  <td>
                    <span style=""font-size: 24px; font-weight: 800; color: #ffffff; letter-spacing: -0.5px;"">SKYER<span style=""color: #a855f7;"">.</span></span>
                    <div style=""font-size: 13px; color: #94a3b8; margin-top: 4px;"">Bespoke Agentic Workforce Engineering</div>
                  </td>
                  <td align=""right"">
                    <span style=""display: inline-block; padding: 6px 14px; background: rgba(168, 85, 247, 0.15); border: 1px solid rgba(168, 85, 247, 0.4); border-radius: 20px; font-size: 12px; color: #c084fc; font-weight: 600;"">{badge}</span>
                  </td>
                </tr>
              </table>
            </td>
          </tr>
          <tr>
            <td style=""padding: 40px;"">
              <h2 style=""font-size: 20px; color: #ffffff; margin-top: 0; margin-bottom: 16px; font-weight: 700;"">{greeting}</h2>
              <p style=""font-size: 15px; line-height: 1.6; color: #cbd5e1; margin-bottom: 24px;"">
                {intro}
              </p>
              <div style=""background: #0b0f19; border: 1px solid #1e293b; border-radius: 12px; padding: 22px; margin-bottom: 28px;"">
                <table width=""100%"" border=""0"" cellspacing=""0"" cellpadding=""0"" style=""margin-bottom: 14px;"">
                  <tr>
                    <td style=""font-size: 13px; color: #94a3b8; width: 140px; padding-bottom: 8px;"">{labelRole}</td>
                    <td style=""font-size: 14px; color: #ffffff; font-weight: 700; padding-bottom: 8px;"">{WebUtility.HtmlEncode(agentRole)}</td>
                  </tr>
                  <tr>
                    <td style=""font-size: 13px; color: #94a3b8; padding-bottom: 8px;"">{labelInfra}</td>
                    <td style=""font-size: 13px; color: #38bdf8; font-weight: 600; padding-bottom: 8px;"">{valInfra}</td>
                  </tr>
                  <tr>
                    <td style=""font-size: 13px; color: #94a3b8;"">{labelTools}</td>
                    <td style=""font-size: 13px; color: #e2e8f0;"">{valTools}</td>
                  </tr>
                </table>
                <div style=""border-top: 1px solid #1e293b; padding-top: 14px; margin-top: 6px;"">
                  <div style=""font-size: 12px; font-weight: 700; color: #94a3b8; text-transform: uppercase; margin-bottom: 8px;"">{labelReq}</div>
                  <div style=""font-size: 14px; line-height: 1.6; color: #e2e8f0; font-style: italic;"">
                    {WebUtility.HtmlEncode(requirements).Replace("\n", "<br/>")}
                  </div>
                </div>
              </div>
              <div style=""background: rgba(168, 85, 247, 0.08); border-left: 4px solid #a855f7; border-radius: 6px; padding: 14px 18px; margin-bottom: 30px;"">
                <p style=""font-size: 13px; line-height: 1.5; color: #d8b4fe; margin: 0;"">
                  {nextSteps}
                </p>
              </div>
              <table width=""100%"" border=""0"" cellspacing=""0"" cellpadding=""0"">
                <tr>
                  <td align=""center"">
                    <a href=""https://skyer.vn/marketplace"" style=""display: inline-block; background: linear-gradient(135deg, #9333ea 0%, #4f46e5 100%); color: #ffffff; text-decoration: none; font-size: 14px; font-weight: 600; padding: 14px 32px; border-radius: 8px; box-shadow: 0 4px 15px rgba(147, 51, 234, 0.35);"">{ctaText}</a>
                  </td>
                </tr>
              </table>
            </td>
          </tr>
          <tr>
            <td style=""padding: 24px 40px; background-color: #0b0f19; border-top: 1px solid #1e293b; text-align: center;"">
              <p style=""font-size: 12px; color: #64748b; margin: 0 0 8px 0;"">
                {footerNote}
              </p>
              <p style=""font-size: 11px; color: #475569; margin: 0;"">
                © {DateTime.UtcNow.Year} Skyer VN. Autonomous Agentic Systems for Enterprises.
              </p>
            </td>
          </tr>
        </table>
      </td>
    </tr>
  </table>
</body>
</html>";
    }
}
