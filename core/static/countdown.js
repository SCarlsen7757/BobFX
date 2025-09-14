function connectCountdownWs() {
    const ws = new WebSocket(`ws://${location.host}/countdown/ws`);
    ws.onmessage = event => {
        const data = JSON.parse(event.data);
        document.getElementById("countdown").value = data.countdown;
    };
    ws.onclose = () => {
        setTimeout(connectCountdownWs, 1000); // reconnect
    };
}
connectCountdownWs();

async function startCountdown(minutes  = 1) {
    const response = await fetch("/countdown/start", {
        method: 'POST',
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ minutes: minutes })
    });
    const data = await response.json();
}

async function stopCountdown() {
    const response = await fetch("/countdown/stop", { method: 'POST' });
    const data = await response.json();
}