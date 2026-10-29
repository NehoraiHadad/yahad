namespace Yahad.Service;

internal enum RuntimeConnectorProfile { Production,Synthetic }
internal readonly record struct RuntimeConnectorSettings(string Connector,string Approval)
{
 // This enables the existing connector, not an account connection. A new family
 // remains unlinked until its authenticated parent uses POST /api/parent/connect
 // in the existing web UI (origin + CSRF checked). Linked families retain their
 // previously requested reconnect behavior. No native QR/account setup is added.
 internal static RuntimeConnectorSettings For(RuntimeConnectorProfile profile)=>profile switch
 {
  RuntimeConnectorProfile.Production=>new("baileys","I_APPROVE_UNOFFICIAL_LINK"),
  RuntimeConnectorProfile.Synthetic=>new("mock","no"),
  _=>throw new IOException("CONNECTOR_PROFILE_INVALID")
 };
}
