# Sapana’s Kitchen Katta

A small full-stack storefront starter: Angular 8 frontend, ASP.NET Core 9 API, EF Core, and PostgreSQL (Neon-compatible).

## Prerequisites

- .NET 9 SDK
- Node.js and npm
- A PostgreSQL database, such as Neon

## Run locally

1. In Neon, create a project/database and copy its connection string. Set it as the `ConnectionStrings__Store` environment variable for the API. Neon `postgresql://` URLs are converted for Npgsql automatically. For local development, keep it in user secrets or your shell environment; do not commit credentials.
2. From the repository root, start the API with `cd .\backend` followed by `dotnet run --launch-profile http`. It listens at `http://localhost:5065`.
3. In a second terminal, install frontend dependencies if needed with `npm --prefix .\frontend install`, then start Angular with `npm --prefix .\frontend start`. Open `http://localhost:4200`.

On first start, EF Core creates the PostgreSQL tables and adds sample products if the product table is empty. Products, uploaded product-image bytes, orders, order items, payment status, and admin accounts are stored in Neon. The image upload accepts JPG, PNG, GIF, and WebP up to 5 MB. The existing local SQLite database is not copied to Neon automatically; export/import any data you need to retain before switching.

The admin dashboard requires sign-in. In Development only, the demo account is `admin` / `KitchenKatta@123`. In Production, provision the first administrator by setting `Admin__UserName` and `Admin__Password` in the service environment before first start. The password is stored as a salted PBKDF2 hash. Keep database and admin credentials in environment secrets, not source control.

Prices are in Indian rupees (INR). The Angular app uses the supplied Sapana’s Kitchen Katta logo and keeps the cart in browser local storage. To enable UPI QR checkout, configure `upiVpa` and `upiPayeeName` in the frontend environment files. Each order reserves stock and gives the customer a private, tab-scoped payment-resume token. Customers can reopen the QR after navigation or refresh, copy the UPI ID, and must enter a UPI transaction/UTR reference (6–100 characters) to report payment. Payment status stays **Pending** when a customer reports the UTR; only an admin can change the status. Admins compare the report/reference with the bank statement before marking an order Paid or Failed; the app records the reviewer and review timestamp. Orders have no payment timeout and keep their reserved stock until an admin changes the status. There is no automatic UPI verification or payment gateway integration.

## Admin sign-in

For local development, the API creates a demo administrator if one does not already exist:

- Username: `admin`
- Password: `KitchenKatta@123`

Admin sign-in returns a random eight-hour bearer token. The browser keeps it in tab-scoped session storage and sends it in the Authorization header for admin requests, so deployed admin access does not rely on third-party cookies (including in InPrivate mode). Only its SHA-256 hash is stored in PostgreSQL's `AdminSessions` table; logout revokes it. Protect the frontend from injected scripts, since JavaScript in the same origin can access tab storage. The API must allow the exact frontend origin through CORS. To change credentials, sign in to Admin, select **Change login**, enter the current password, then set a username (3–100 characters, no spaces) and a new password (at least 12 characters). Changing credentials revokes other admin sessions and issues a new token for this tab. The administrator record is stored in the PostgreSQL `AdminUsers` table (`UserName`, `NormalizedUserName`, `PasswordHash`, and `CreatedAt`). Passwords are hashed using PBKDF2-SHA256 with a unique random salt; the plaintext password is not stored. The demo account is seeded only in Development. In Production, set `Admin__UserName` and `Admin__Password` before first startup to provision the initial administrator; change the demo credentials before deployment.

## Build

Run `dotnet build .\backend\backend.csproj` and `npm --prefix .\frontend run build` from the repository root. The backend's HTTP profile is configured in `backend/Properties/launchSettings.json`; update the frontend API URL in `frontend/src/environments/` if you change that port.

## Deploy the API on Render

Create a Render Web Service from this repository using the Docker runtime. Set the service root directory to `backend` so Render uses `backend/Dockerfile`. Configure these environment variables in Render: `ConnectionStrings__Store` with the Neon connection string, `Admin__UserName`, and `Admin__Password`. The API CORS policy allows the current frontend `https://shoppingwebsite-1-9ytf.onrender.com`; override it with `FrontendOrigin` (or comma-separated `FrontendOrigins`) if the frontend domain changes. The container binds to Render's `PORT` (default 10000). Deploy the Angular app separately as a Static Site from the `frontend` directory, with build command `npm install && npm run build` and publish directory `dist/frontend`. Set the API URL in `frontend/src/environments/environment.prod.ts` to the deployed API URL ending in `/api` before building the Static Site.
