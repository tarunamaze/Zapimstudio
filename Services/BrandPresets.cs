using Microsoft.EntityFrameworkCore;

namespace Zapim.Marketing;

public record BrandPreset(string Name, string Website, string Color, string Description, string Audience, string Tone);

// Further brands of the group, written from their websites (October 2026). Each lists only facts its site states; claims that
// conflict on the site or are template filler are left out. Created once at startup; never recreated or overwritten.
public static class BrandPresets
{
    public static readonly BrandPreset[] All =
    [
        new("Vesper Telecom", "https://vespertelecom.com", "#3A46D1",
            "Vesper Telecom (vespertelecom.com) is a B2B cloud communications company (CPaaS, A2P messaging and voice). Corporate office: Dubai Silicon Oasis, UAE; registered office in Hong Kong. It routes SMS across South Asia, North Africa, Europe, the Middle East and the Americas, for small and medium businesses, large enterprises and carriers.\n" +
            "Messaging: bulk SMS, two-way SMS, OTP and transactional SMS, DND SMS, SMS route provisioning, wholesale SMS termination, SMS fraud management and firewall; WhatsApp Business API, Viber, LINE, Telegram and Facebook Messenger; email marketing.\n" +
            "Voice and numbers: bulk voice calls, two-way voice, VoIP, DID numbers, toll-free numbers and virtual mobile numbers for a local-to-global presence.\n" +
            "Published facts (use exactly, never invent other numbers): 99.9% uptime guarantee with redundant infrastructure; 98%+ delivery success rate; routes millions of SMS messages daily; 152 happy clients; up to 70% cost reduction on bulk messaging; 24/7 technical support via online chat and email, 365 days a year; free test credits and a dedicated account manager. Pricing depends on traffic destination; no prices are published.\n" +
            "Content pillars: bulk and A2P SMS (delivery, OTP, route quality); omnichannel messaging APIs (WhatsApp, Viber, LINE, Telegram, Messenger); voice and virtual numbers; compliance and trust (opt-in, consent, DND, fraud firewall); industry use cases (e-commerce, customer service centres, SaaS, nonprofits); AI and automation in messaging; reliability and support.\n" +
            "Call to action: contact sales at https://vespertelecom.com/contact-us-exceptional-cloud-services/ (info@vespertelecom.com). Social: facebook.com/vespertelecom1, instagram.com/vespertelecom.",
            "Business owners, marketing, customer experience and operations teams at SMBs and large enterprises, SaaS and e-commerce companies, customer service centres, and telecom carriers and resellers buying wholesale SMS and voice, across the Middle East, South Asia, Africa, Europe and the Americas.",
            "Confident, enterprise B2B voice about reliability, scale, compliance and global reach, warmer and more conversational in explainers. Headlines in the style of 'Cloud solutions built for businesses that think big'. Plain explanations over buzzwords. Never invent statistics, customer names or prices."),
        new("Welco Infotech", "https://welcoinfotech.com", "#0A2968",
            "Welco Infotech Pvt. Ltd. (welcoinfotech.com, tagline 'Your Success Partner') is a Delhi-based VoIP carrier and business communications provider, with offices in New Delhi (Naraina Industrial Area) and Gurugram (MG Road). It holds a Class A ISP/ITSP licence from India's Department of Telecommunications.\n" +
            "VoIP: colocation, outbound VoIP termination, hosted PBX, IVR systems, and dialers with CRM integration for call centres.\n" +
            "SMS: bulk SMS, OTP verification, appointment reminders, transactional and promotional SMS, SMS API (HTTP and SMPP), DLT registration, and messages in Tamil, English, Hindi and other regional languages.\n" +
            "Hosted services: UCaaS and hosted unified communications, a cost-effective alternative to on-premise systems for SMEs.\n" +
            "Published facts (use exactly, never invent other numbers or a founding year): terminates millions of call centre minutes each day; over 1000+ unique routes; 99% server uptime; service 24x7x365; 100% delivery for active mobile numbers; no setup fee, no hidden cost, no monthly rent.\n" +
            "Content pillars: VoIP and carrier quality (routes, call clarity, uptime); call-centre productivity (dialers, CRM integration, IVR, hosted PBX); SMS for business (OTP, transactional vs promotional, reminders, DLT compliance); moving to the cloud with UCaaS; developer angle (SMS API, HTTP/SMPP); trust and credentials (DoT licence, 24x7 support, Delhi NCR presence); practical tips for call centres and messaging.\n" +
            "Call to action: enquire at https://welcoinfotech.com/contact (info@welcoinfotech.com, replies within 24 hours). Social: linkedin.com/company/welco-infotech, facebook.com/Welco-Infotech-142294532498603.",
            "Call centres and BPOs, corporates, digital marketing businesses, SMEs moving to cloud telephony, and VoIP solution providers in India, especially Delhi NCR: operations heads, IT and telecom managers, and founders.",
            "Practical, reassuring, sales-led B2B telecom voice focused on reliability, cost savings and scale. Explain jargon (tier-1 termination, DLT, SMPP) in plain words. Short, clear headlines such as 'Voice is the best way to reach your customers.' Never invent statistics, customer names or prices."),
    ];

    // Adds each preset brand unless a brand with the same website or name already exists, so a brand is created only once
    // and nothing an administrator has created or edited is touched.
    public static async Task Ensure(MarketingDb db)
    {
        var existing = await db.Brands.Select(b => new { b.Name, b.Website }).ToListAsync();
        static string Host(string url) => Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host.ToLowerInvariant().Replace("www.", "") : url.Trim().ToLowerInvariant();
        foreach (var p in All.Where(p => !existing.Any(e => Host(e.Website) == Host(p.Website) || e.Name.Equals(p.Name, StringComparison.OrdinalIgnoreCase))))
            db.Brands.Add(new Brand { Name = p.Name, Website = p.Website, Color = p.Color, Description = p.Description, Audience = p.Audience, Tone = p.Tone });
        await db.SaveChangesAsync();
    }
}
