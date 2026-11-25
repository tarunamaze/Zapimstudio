namespace Zapim.Marketing;

// Brand context for Zapim, written from zapim.com (October 2026). It is what the AI plans and writes from, so it lists only
// facts the site states; the published figures are quoted so the AI can use them exactly and never invent others.
public static class ZapimContext
{
    public const string Color = "#0038C9";
    public const string Website = "https://zapim.com";

    public const string Description =
        "Zapim (zapim.com) is an AI-powered CPaaS platform for smarter business communication, based in Noida, India and founded in 2020. " +
        "One dashboard and one set of APIs for the WhatsApp Business API (official Meta Business Solution Provider), SMS (transactional, promotional, two-way and OTP/2FA), RCS, voice (IVR, voice broadcast, Elastic SIP Trunking, Voice SDK) and email.\n" +
        "AI: AI Agents for lead generation, product discovery, collections, order management and customer support; an AI Voice Agent that is multilingual, works with CRM data and detects fraud and deepfakes; rule-based and AI chatbots; marketing automation and campaign management. Also DLT registration assistance for India.\n" +
        "Platform: one conversation thread per customer across channels, smart routing with RCS-to-SMS fallback, a message builder with live preview, delivery analytics exportable as PDF, Excel or CSV, REST APIs and webhooks, pay-as-you-go pricing and instant onboarding.\n" +
        "Published facts (use exactly, never invent other numbers): 10+ communication channels; 500+ customers; 99.9% uptime guarantee; 24/7 tech support; 100+ TPS; voice live in 100+ countries; founded in 2020; GDPR compliant; bank-grade encryption.\n" +
        "Industries: banks and NBFCs, retail and e-commerce (D2C brands, marketplaces), healthcare, travel and hospitality, logistics, automotive, consumer goods.\n" +
        "Content pillars: one platform for every channel; compliance made simple in India (DLT, TRAI, WhatsApp Business API vs the Business app); AI agents and voice AI; industry playbooks (cart recovery, COD verification to reduce RTO, appointment reminders, collections); channel explainers (what RCS is, RCS vs SMS, transactional vs promotional SMS); analytics and reliability; festive and seasonal campaign ideas for India.\n" +
        "Calls to action: request a demo at https://zapim.com/request-demo. Social: linkedin.com/company/zapim, instagram.com/zapim_com, facebook.com/zapimofficial, x.com/zapim_com.";

    public const string Audience =
        "Marketing, CRM, customer support and product leaders, developers and compliance teams at Indian and global businesses, especially banks and NBFCs, D2C and e-commerce brands, healthcare, travel, logistics and automotive companies.";

    public const string Tone =
        "Confident, practical, helpful B2B voice. Short, punchy headlines in the style of 'Connect. Communicate. Convert.' and 'Automate. Personalise. Scale.' " +
        "Concrete use cases over hype, plain explanations of jargon, British/Indian English spelling (personalise, optimise). Never invent statistics, customer names or results.";

    // The starter brand created before this context existed; upgraded only while it is still unedited.
    public const string StarterDescription = "Business communication through WhatsApp, SMS, RCS, voice and email. Campaigns, support and transactional messaging in one platform.";
    public const string StarterAudience = "Marketing, customer experience and product leaders at Indian businesses";
    public const string StarterTone = "Confident, helpful, practical. Explain business outcomes clearly; avoid unsupported statistics.";
    public const string StarterColor = "#087f76";
}
