// Dev-server proxy to the backend. Both the `http` and the Visual Studio `https` launch profiles serve
// http://localhost:5089, and in Development the backend does not redirect to HTTPS: a 307 there broke
// multipart uploads (POST /files ended in ERR_CONNECTION_RESET). Another backend: API_URL=... npm start
const target = process.env.API_URL ?? 'http://localhost:5089';

export default Object.fromEntries(
  ['/profiles', '/files', '/jobs', '/document', '/audio'].map((path) => [
    path,
    { target, secure: false, changeOrigin: true },
  ]),
);
