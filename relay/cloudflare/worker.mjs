const maxRequestBytes = 8192;
const maxDescriptionLength = 1200;

export default {
    async fetch(request, env) {
        if (new URL(request.url).pathname !== "/reports") {
            return new Response(null, { status: 404 });
        }
        if (request.method !== "POST") {
            return new Response(null, { status: 405, headers: { Allow: "POST" } });
        }
        if (!/^\d+:[A-Za-z0-9_-]+$/.test(env.TELEGRAM_BOT_TOKEN ?? "") ||
            !/^-?[0-9]+$/.test(env.TELEGRAM_CHAT_ID ?? "")) {
            return new Response(null, { status: 503 });
        }

        try {
            const ip = request.headers.get("CF-Connecting-IP") ?? "unknown";
            if (!(await env.PER_SENDER_LIMIT.limit({ key: ip })).success) return rateLimited();

            const report = await readReport(request);
            if (!report) return new Response(null, { status: 400 });
            if (!(await env.TOTAL_LIMIT.limit({ key: "reports" })).success) return rateLimited();

            const telegram = await fetch(`https://api.telegram.org/bot${env.TELEGRAM_BOT_TOKEN}/sendMessage`, {
                method: "POST",
                redirect: "manual",
                headers: { "Content-Type": "application/json" },
                body: JSON.stringify({
                    chat_id: env.TELEGRAM_CHAT_ID,
                    text: `Обратная связь · RadialMenu ${report.version}\n\n${report.description}`,
                    link_preview_options: { is_disabled: true },
                }),
                signal: AbortSignal.timeout(8000),
            });
            if (telegram.ok && (await telegram.json()).ok === true) {
                return Response.json({ delivered: true }, { headers: { "Cache-Control": "no-store" } });
            }
        } catch {
            // The Telegram request URL contains the bot token. Keep provider errors on the server.
        }
        return new Response(null, { status: 502 });
    },
};

function rateLimited() {
    return new Response(null, { status: 429, headers: { "Retry-After": "60" } });
}

async function readReport(request) {
    if (request.headers.get("Content-Type")?.split(";")[0].trim().toLowerCase() !== "application/json" ||
        !request.body || Number(request.headers.get("Content-Length")) > maxRequestBytes) return null;

    const buffer = new Uint8Array(maxRequestBytes);
    const reader = request.body.getReader();
    let length = 0;
    try {
        while (true) {
            const { done, value } = await reader.read();
            if (done) break;
            if (length + value.length > maxRequestBytes) {
                await reader.cancel();
                return null;
            }
            buffer.set(value, length);
            length += value.length;
        }

        const body = JSON.parse(new TextDecoder("utf-8", { fatal: true }).decode(buffer.subarray(0, length)));
        const description = typeof body?.description === "string" ? body.description.trim() : "";
        const version = body?.version;
        if (description.length === 0 || description.length > maxDescriptionLength ||
            typeof version !== "string" || !/^\d{1,10}(\.\d{1,10}){1,3}$/.test(version)) return null;
        return { description, version };
    } catch {
        return null;
    } finally {
        reader.releaseLock();
    }
}
