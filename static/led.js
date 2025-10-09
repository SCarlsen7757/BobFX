function connectLedWs() {
    const ws = new WebSocket(`ws://${location.host}/led/ws`);
    ws.onmessage = event => {
        const colors = JSON.parse(event.data);
        renderLeds(colors, 'led-strip');
    };
    ws.onclose = () => {
        setTimeout(connectLedWs, 1000); // reconnect
    };
}

connectLedWs();

function renderLeds(colors, containerId) {
    const cols = 32
    const container = document.getElementById(containerId);
    container.innerHTML = "";

    for (let row = 0; row < 8; row++) {
        const reverse = row % 2 === 1; // reverse every other row
        for (let col = 0; col < cols; col++) {
            const index = reverse ? (row + 1) * cols - 1 - col : row * cols + col;
            const led = document.createElement("div");
            led.className = "led";
            led.style.backgroundColor = colors[index];
            container.appendChild(led);
        }
    }
}

async function nextEffect() {
    const response = await fetch("/led/effect/next", { method: 'POST' });
    const data = await response.json();
}

async function prevEffect() {
    const response = await fetch("/led/effect/prev", { method: 'POST' });
    const data = await response.json();
}

async function sendColor() {
      const color = document.getElementById("colorPicker").value;
      await fetch("/led/effect/color", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ color: color })
      });
    }