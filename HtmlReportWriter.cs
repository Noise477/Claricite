using System;
using System.Net;
using System.Text;

namespace ClariCite
{
   public sealed class HtmlReportWriter
   {
      private readonly StringBuilder _sb = new();

      public HtmlReportWriter Heading(string text, int level = 1)
      {
         level = Math.Clamp(level, 1, 6);

         _sb.Append("<h")
            .Append(level)
            .Append('>')
            .Append(WebUtility.HtmlEncode(text))
            .Append("</h")
            .Append(level)
            .AppendLine(">");

         return this;
      }

      public HtmlReportWriter Paragraph(string text)
      {
         _sb.Append("<p>")
            .Append(WebUtility.HtmlEncode(text))
            .AppendLine("</p>");

         return this;
      }

      public HtmlReportWriter HorizontalRule()
      {
         _sb.AppendLine("<hr>");

         return this;
      }

      public HtmlReportWriter Property(string name, string? value)
      {
         _sb.Append("<p><strong>")
            .Append(WebUtility.HtmlEncode(name))
            .Append(":</strong> ")
            .Append(WebUtility.HtmlEncode(value ?? "-"))
            .AppendLine("</p>");

         return this;
      }

      public HtmlReportWriter LinkProperty(string name, string? url)
      {
         _sb.Append("<p><strong>")
            .Append(WebUtility.HtmlEncode(name))
            .Append(":</strong> ");

         if (string.IsNullOrWhiteSpace(url))
         {
            _sb.Append("-");
         }
         else
         {
            _sb.Append("<a href=\"")
               .Append(WebUtility.HtmlEncode(url))
               .Append("\">")
               .Append(WebUtility.HtmlEncode(url))
               .Append("</a>");
         }

         _sb.AppendLine("</p>");

         return this;
      }

      public HtmlReportWriter DoiProperty(string? doi)
      {
         _sb.Append("<p><strong>DOI:</strong> ");

         if (string.IsNullOrWhiteSpace(doi))
         {
            _sb.Append("-");
         }
         else
         {
            _sb.Append("<a href=\"https://doi.org/")
               .Append(WebUtility.HtmlEncode(doi))
               .Append("\">")
               .Append(WebUtility.HtmlEncode(doi))
               .Append("</a>");
         }

         _sb.AppendLine("</p>");

         return this;
      }

      public override string ToString()
      {
         return _sb.ToString();
      }

   }
}