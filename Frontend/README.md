# Frontend — AI Toy (Wake up + Hold mic to speak)

## How to open in the browser (localhost)

### Option 1: Use the Backend (recommended)

1. **Start the Backend** from the project root:
   ```bash
   cd Backend
   dotnet run
   ```
2. **Open in your browser:**
   - **URL:** **http://localhost:5000**
   - Or from another device on the same network: **http://YOUR_PC_IP:5000** (e.g. http://192.168.1.10:5000)

The Backend serves this frontend from `Backend/wwwroot/index.html`, so you get the same Wake up + mic UI.

---

### Option 2: Open the HTML file directly

- **File path:** `Frontend/index.html`
- **In browser:** Open the file (e.g. drag `index.html` into Chrome, or **File → Open file** and select `Frontend/index.html`).
- **URL will look like:** `file:///D:/Project/Frontend/index.html`

**Note:** The WebSocket URL in the page defaults to `ws://localhost:5000/ws`. For this to work, the Backend must be running. If you use a different port or host, change the “WebSocket URL” field on the page.

---

### Option 3: Run a small static server (no Backend yet)

If you only want to view the UI without running the Backend:

```bash
cd Frontend
npx serve -p 3000
```

Then open **http://localhost:3000** in the browser. You’ll still need the Backend running (on port 5000) for “Wake up” and the mic to work.

---

## Summary

| How you open it        | URL / Location                          |
|------------------------|-----------------------------------------|
| **Backend running**    | **http://localhost:5000**               |
| **Same network (phone)** | http://YOUR_PC_IP:5000 (e.g. 192.168.1.10) |
| **File open**          | file:///.../Frontend/index.html         |
| **npx serve**         | http://localhost:3000                   |

For full functionality (Wake up + mic → AI response), run the Backend and use **http://localhost:5000**.
