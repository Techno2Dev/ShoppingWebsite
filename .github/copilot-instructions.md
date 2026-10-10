# Workspace notes

- Full-stack shop starter: Angular frontend in `frontend/`; ASP.NET Core 9 API in `backend/`.
- Persistence uses EF Core with SQLite (`backend/store.db`); connection string is configured in `backend/appsettings.json`.
- API uses `http://localhost:5065`; Angular development server uses `http://localhost:4200`.
- Product browsing, local cart, guest checkout, stock validation, and a demo admin dashboard are implemented.
- Build and run instructions are in the repository-root `README.md`.
- VS Code build task: **Build frontend and backend**.
- Do not launch either development server unless the user requests it.
