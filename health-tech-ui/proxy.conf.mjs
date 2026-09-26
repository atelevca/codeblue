// Dev-server proxy to the backend. `dotnet run --launch-profile http` serves http://localhost:5089;
// the Visual Studio `https` profile redirects that port to HTTPS, so point the proxy there instead:
//   API_URL=https://localhost:7059 npm start
const target = process.env.API_URL ?? 'http://localhost:5089';

export default Object.fromEntries(
  ['/profiles', '/files', '/jobs', '/document', '/audio'].map((path) => [
    path,
    { target, secure: false, changeOrigin: true },
  ]),
);
