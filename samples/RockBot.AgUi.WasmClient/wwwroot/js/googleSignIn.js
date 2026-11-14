// App mode only: Google Identity Services, loaded when the sample asks for it.
// https://developers.google.com/identity/gsi/web/guides/overview

let loading;

function loadGoogle() {
  loading ??= new Promise((resolve, reject) => {
    const script = document.createElement('script');
    script.src = 'https://accounts.google.com/gsi/client';
    script.async = true;
    script.onload = resolve;
    script.onerror = () => reject(new Error('Could not load Google Identity Services.'));
    document.head.appendChild(script);
  });
  return loading;
}

// Draws Google's button and hands the ID token (the "credential") to .NET when the user signs in.
export async function show(clientId, button, dotnet) {
  await loadGoogle();
  google.accounts.id.initialize({
    client_id: clientId,
    auto_select: true,
    callback: response => dotnet.invokeMethodAsync('OnCredential', response.credential),
  });
  google.accounts.id.renderButton(button, { theme: 'outline', size: 'large' });
  google.accounts.id.prompt();
}

export function signOut() {
  if (window.google?.accounts?.id) {
    google.accounts.id.disableAutoSelect();
  }
}
