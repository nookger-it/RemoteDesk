import type { Metadata } from "next";

export const metadata: Metadata = {
  title: "RemoteDesk Dashboard — LAN-First V1",
  description: "Management console for LAN-First Remote Desktop devices and sessions",
};

export default function RootLayout({
  children,
}: Readonly<{
  children: React.ReactNode;
}>) {
  return (
    <html lang="th">
      <body style={{ fontFamily: "system-ui, sans-serif", margin: 0, padding: "24px", backgroundColor: "#0f172a", color: "#f8fafc" }}>
        {children}
      </body>
    </html>
  );
}

