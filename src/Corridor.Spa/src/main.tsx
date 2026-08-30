import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import { AuthProvider } from "./auth/AuthContext";
import { createUserManager } from "./auth/userManager";
import { App } from "./App";
import "./styles.css";

const manager = createUserManager();

createRoot(document.getElementById("root")!).render(
  <StrictMode>
    <AuthProvider manager={manager}>
      <App manager={manager} />
    </AuthProvider>
  </StrictMode>,
);
