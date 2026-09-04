package uk.co.adamkhattab.pcconnect.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.text.SpanStyle
import androidx.compose.ui.text.buildAnnotatedString
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.input.PasswordVisualTransformation
import androidx.compose.ui.text.withStyle
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.ui.window.Dialog
import uk.co.adamkhattab.pcconnect.data.CommandTypes

/**
 * The confirmation a destructive command needs (ADR-0011).
 *
 * It names the actual consequence — "Shut down Study PC?" — rather than asking
 * someone to confirm an abstraction. A dialog that says "Are you sure?" teaches
 * people to press yes.
 *
 * The password is asked for every time, because the server checks it: the
 * fingerprint is a local gate in front of that check, not a replacement for it.
 * The design also draws a fingerprint-only variant; that needs a passkey
 * assertion the server would accept in place of a password, and this app does
 * not register passkeys yet.
 */
@Composable
fun ConfirmCommandDialog(
    pending: PendingCommand,
    biometricGate: Boolean,
    error: String?,
    busy: Boolean,
    onConfirm: (String) -> Unit,
    onDismiss: () -> Unit,
) {
    var password by remember(pending) { mutableStateOf("") }

    Dialog(onDismissRequest = onDismiss) {
        Column(
            Modifier
                .fillMaxWidth()
                .clip(PcShapes.Dialog)
                .background(PcColors.Surface)
                .padding(start = 20.dp, end = 20.dp, top = 24.dp, bottom = 20.dp),
            verticalArrangement = Arrangement.spacedBy(14.dp),
        ) {
            Box(
                Modifier
                    .size(48.dp)
                    .clip(RoundedCornerShape(14.dp))
                    .background(PcColors.DangerBg),
                contentAlignment = Alignment.Center,
            ) {
                PcIcon(PcIcons.forCommand(pending.type), null, size = 26.dp, tint = PcColors.Danger)
            }

            Text(
                "${CommandTypes.label(pending.type)} ${pending.deviceName}?",
                color = PcColors.Ink,
                style = PcType.Heading.copy(fontSize = 21.sp),
            )

            Text(
                buildAnnotatedString {
                    append(
                        "This ends whatever is running there. Because you're already " +
                            "signed in, it still needs your ",
                    )
                    withStyle(SpanStyle(fontWeight = FontWeight.SemiBold, color = PcColors.Ink)) {
                        append("PCConnect password")
                    }
                    append(" — not the PC's Windows password.")
                },
                color = PcColors.InkSoft,
                style = PcType.BodySmall.copy(lineHeight = 21.sp),
            )

            PcTextField(
                value = password,
                onValueChange = { password = it },
                label = "PCConnect password",
                height = 50.dp,
                isError = error != null,
                // The mask hides it on screen; the password keyboard type keeps
                // it out of the suggestion strip and the learned-words
                // dictionary, where it would outlive the dialog.
                keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Password),
                visualTransformation = PasswordVisualTransformation(),
            )

            if (error != null) {
                Text(error, color = PcColors.DangerInk, style = PcType.Caption)
            }

            if (biometricGate) {
                Row(
                    verticalAlignment = Alignment.CenterVertically,
                    horizontalArrangement = Arrangement.spacedBy(6.dp),
                ) {
                    PcIcon(PcIcons.Fingerprint, null, size = 16.dp, tint = PcColors.InkFaint)
                    Caption("This phone will ask for your fingerprint as well.")
                }
            }

            Row(horizontalArrangement = Arrangement.spacedBy(10.dp)) {
                QuietButton("Cancel", onDismiss, Modifier.weight(1f), height = 48.dp)

                Box(Modifier.weight(1f)) {
                    PrimaryButton(
                        text = CommandTypes.label(pending.type),
                        onClick = { onConfirm(password) },
                        enabled = password.isNotEmpty() && !busy,
                        height = 48.dp,
                        container = PcColors.Danger,
                    )
                }
            }
        }
    }
}
